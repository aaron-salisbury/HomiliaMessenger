using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Integrations;
using HomiliaMessenger.Integrations.Embeddings;
using HomiliaMessenger.Integrations.ImageGeneration;
using HomiliaMessenger.Integrations.LocalModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.ImageGeneration;

/// <summary>
/// The image model catalog, downloads, the model in use, and removal.
/// </summary>
[TestClass]
public sealed class ImageModelLibraryTests
{
    private const string REPO = "test/portraits";
    private const string REVISION = "0123456789abcdef0123456789abcdef01234567";
    private static readonly byte[] FIRST = Encoding.UTF8.GetBytes("first image model weights");
    private static readonly byte[] SECOND = Encoding.UTF8.GetBytes("second image model weights, a little longer");
    private static readonly byte[] LICENSE = Encoding.UTF8.GetBytes("A license.");

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Catalog_OffersDreamShaperPinnedAndVerified()
    {
        ImageModelDefinition model = ImageModelCatalog.Models.Single(model => model.Processor == ImageProcessor.Cpu);

        Assert.AreEqual(ImageModelCatalog.DREAMSHAPER_8_LCM, model.Id);
        Assert.AreEqual("DreamShaper 8 LCM", model.DisplayName);
        Assert.StartsWith("Generates polished, slightly stylized photographic portraits locally.", model.Description);
        Assert.AreEqual("CreativeML Open RAIL-M", model.License);
        Assert.IsTrue(model.IsVerified);
        Assert.IsFalse(model.IsUserAdded);
        Assert.MatchesRegex(new Regex("^https://huggingface.co/NishQ1/vora-create-sd15/resolve/[0-9a-f]{40}/$"), model.BaseUrl);
        Assert.AreEqual("vora-create-dreamshaper8-lcm-q8_0.gguf", model.Checkpoint.Name);
        Assert.AreEqual(1_802_206_336L, model.Checkpoint.Size);
        Assert.AreEqual("dd06826d1eca711efa38190395a5ee949d0427fc6422c39433358a815e5b066c", model.Checkpoint.Sha256);
        Assert.AreEqual("LICENSE.txt", model.Files[1].Name);
        Assert.AreEqual(1_802_206_336L + 14_385L, model.DownloadBytes);
        Assert.AreEqual(new ImageGenerationSettings(512, 512, "lcm", null, 4, 1f), model.Settings);
    }

    [TestMethod]
    public void Catalog_ModelsAreNeverConversationalOrEmbeddingModels()
    {
        IEnumerable<string> others = LocalModelCatalog.Models.Select(model => model.Id).Concat(EmbeddingModelCatalog.Models.Select(model => model.Id));

        Assert.DoesNotContain(model => others.Contains(model.Id, StringComparer.OrdinalIgnoreCase), ImageModelCatalog.Models);
    }

    [TestMethod]
    public void Composition_RegistersImageGenerationApartFromTheAgents()
    {
        using TestFolder folder = new();
        ServiceCollection services = new();
        services.AddLogging().AddHttpClient();
        services.RegisterInternalIntegrationsServices(folder.Path);
        using ServiceProvider provider = services.BuildServiceProvider();

        IImageGenerator generator = provider.GetRequiredService<IImageGenerator>();
        IReadOnlyList<ImageModel> images = provider.GetRequiredService<IImageModelLibrary>().List();
        IReadOnlyList<LocalModel> conversational = provider.GetRequiredService<ILocalModelLibrary>().List();
        IEnumerable<IConversationAgent> agents = provider.GetServices<IConversationAgent>();

        Assert.IsInstanceOfType<StableDiffusionImageGenerator>(generator);
        Assert.IsFalse(generator.GetAvailability().IsReady);
        Assert.AreSequenceEqual(new[] { ImageModelCatalog.DREAMSHAPER_8_LCM, ImageModelCatalog.FLUX2_KLEIN_4B, ImageModelCatalog.DREAMSHAPER_XL_LIGHTNING }, images.Select(image => image.Id).ToArray());
        Assert.DoesNotContain(image => image.IsDownloaded, images);
        Assert.AreEqual(ImageProcessor.Cpu, images[0].Processor);
        Assert.IsTrue(images.Skip(1).All(image => image.Processor == ImageProcessor.Gpu));
        Assert.DoesNotContain(model => images.Any(image => image.Id == model.Id), conversational);
        Assert.DoesNotContain(agent => agent is IImageGenerator, agents);
        Assert.IsFalse(Directory.Exists(Path.Combine(folder.Path, "Models", "Images")), "Nothing is downloaded or created until asked.");
    }

    [TestMethod]
    public async Task Download_VerifiesInstallsAndUsesTheFirstModelAtOnce()
    {
        using TestFolder folder = new();
        FakeHub hub = Hub();
        ImageModelLibrary library = CreateLibrary(folder.Path, hub);
        List<LocalModelDownloadProgress> progress = [];

        LocalModelDownloadResult result = await library.DownloadAsync("first", progress: new SynchronousDownloadProgress(progress), cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(LocalModelDownloadStatus.Success, result.Status);
        Assert.AreEqual("first", library.Active?.Id);
        Assert.IsTrue(library.List().Single(model => model.Id == "first").IsSelected);
        Assert.AreSequenceEqual(FIRST, File.ReadAllBytes(library.CheckpointPath(library.Active!)));
        Assert.IsFalse(Directory.Exists(library.ModelDirectory(library.Active!) + LocalModelLibrary.PARTIAL_SUFFIX));
        Assert.AreEqual(FIRST.Length + LICENSE.Length, progress[^1].BytesDownloaded);
        Assert.Contains($"{REPO}/resolve/{REVISION}/first.gguf", hub.Requested);
    }

    [TestMethod]
    public async Task Download_ThatDoesNotMatchItsHash_IsDiscardedAndNotInstalled()
    {
        using TestFolder folder = new();
        FakeHub hub = Hub();
        hub.Files[$"{REPO}/resolve/{REVISION}/first.gguf"] = Encoding.UTF8.GetBytes("first image model weightX");
        ImageModelLibrary library = CreateLibrary(folder.Path, hub);

        LocalModelDownloadResult result = await library.DownloadAsync("first", cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(LocalModelDownloadStatus.Failed, result.Status);
        Assert.Contains("checksum", result.Problem!);
        Assert.IsNull(library.Active);
        Assert.IsFalse(File.Exists(Path.Combine(folder.Path, "first" + LocalModelLibrary.PARTIAL_SUFFIX, "first.gguf")));
    }

    [TestMethod]
    public async Task Download_ThatIsInterrupted_ResumesWhereItStopped()
    {
        using TestFolder folder = new();
        FakeHub hub = Hub();
        hub.CutOffAfter = 10;
        ImageModelLibrary library = CreateLibrary(folder.Path, hub);

        LocalModelDownloadResult interrupted = await library.DownloadAsync("first", cancellationToken: TestContext.CancellationToken);
        long kept = new FileInfo(Path.Combine(folder.Path, "first" + LocalModelLibrary.PARTIAL_SUFFIX, "first.gguf")).Length;
        hub.CutOffAfter = null;
        LocalModelDownloadResult resumed = await library.DownloadAsync("first", cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(LocalModelDownloadStatus.Failed, interrupted.Status);
        Assert.Contains("Try again to resume", interrupted.Problem!);
        Assert.AreEqual(10, kept);
        Assert.AreEqual(LocalModelDownloadStatus.Success, resumed.Status);
        Assert.Contains($"{REPO}/resolve/{REVISION}/first.gguf bytes=10-", hub.Ranges);
        Assert.AreSequenceEqual(FIRST, File.ReadAllBytes(library.CheckpointPath(library.Find("first")!)));
    }

    [TestMethod]
    public async Task Download_Canceled_IsPausedNotInstalled()
    {
        using TestFolder folder = new();
        ImageModelLibrary library = CreateLibrary(folder.Path, Hub());

        LocalModelDownloadResult result = await library.DownloadAsync("first", cancellationToken: new CancellationToken(canceled: true));

        Assert.AreEqual(LocalModelDownloadStatus.Canceled, result.Status);
        Assert.IsNull(library.Active);
    }

    [TestMethod]
    public async Task Download_WithoutEnoughDiskSpace_FailsBeforeDownloading()
    {
        using TestFolder folder = new();
        FakeHub hub = Hub();
        ImageModelDefinition huge = Definition("huge", FIRST) with { Files = [new LocalModelFile("huge.gguf", 1L << 60, FakeHub.Sha256(FIRST))] };
        ImageModelLibrary library = new([huge], folder.Path, hub, new ImageGenerationGate(), NullLogger<ImageModelLibrary>.Instance);

        LocalModelDownloadResult result = await library.DownloadAsync("huge", cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(LocalModelDownloadStatus.Failed, result.Status);
        Assert.Contains("disk space", result.Problem!);
        Assert.IsEmpty(hub.Requested);
    }

    [TestMethod]
    public async Task Selection_IsAutomaticForOneModelAndRememberedWhenChosen()
    {
        using TestFolder folder = new();
        FakeHub hub = Hub();
        ImageModelLibrary library = CreateLibrary(folder.Path, hub);

        await library.DownloadAsync("first", cancellationToken: TestContext.CancellationToken);
        await library.DownloadAsync("second", cancellationToken: TestContext.CancellationToken);
        string? afterBoth = library.Active?.Id;
        bool chosen = library.Select("second");
        bool unknown = library.Select("missing");
        ImageModelLibrary restarted = CreateLibrary(folder.Path, hub);

        Assert.AreEqual("first", afterBoth);
        Assert.IsTrue(chosen);
        Assert.IsFalse(unknown);
        Assert.AreEqual("second", restarted.Active?.Id);
        Assert.ContainsSingle(model => model.IsSelected, restarted.List());
    }

    [TestMethod]
    public async Task Delete_TheModelInUse_FallsBackToAnotherDownloadedModel()
    {
        using TestFolder folder = new();
        ImageModelLibrary library = CreateLibrary(folder.Path, Hub());
        await library.DownloadAsync("first", cancellationToken: TestContext.CancellationToken);
        await library.DownloadAsync("second", cancellationToken: TestContext.CancellationToken);

        bool removed = await library.DeleteAsync("first", cancellationToken: TestContext.CancellationToken);
        string? afterFirst = library.Active?.Id;
        await library.DeleteAsync("second", cancellationToken: TestContext.CancellationToken);

        Assert.IsTrue(removed);
        Assert.AreEqual("second", afterFirst);
        Assert.IsNull(library.Active);
        Assert.IsFalse(Directory.Exists(Path.Combine(folder.Path, "first")));
        Assert.IsFalse(await library.DeleteAsync("missing", cancellationToken: TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Delete_WaitsForAnImageBeingDrawn()
    {
        using TestFolder folder = new();
        ImageGenerationGate gate = new();
        ImageModelLibrary library = new([Definition("first", FIRST)], folder.Path, Hub(), gate, NullLogger<ImageModelLibrary>.Instance);
        await library.DownloadAsync("first", cancellationToken: TestContext.CancellationToken);

        Assert.IsTrue(gate.TryEnter());
        Task<bool> deleting = library.DeleteAsync("first", cancellationToken: TestContext.CancellationToken);
        await Task.Delay(100, TestContext.CancellationToken);
        bool stillThere = File.Exists(library.CheckpointPath(library.Find("first")!));
        gate.Exit();
        await deleting;

        Assert.IsTrue(stillThere);
        Assert.IsFalse(File.Exists(library.CheckpointPath(library.Find("first")!)));
    }

    [TestMethod]
    public async Task Delete_DuringADownload_IsRefusedSoTheFilesAreNeverOrphaned()
    {
        using TestFolder folder = new();
        ImageModelLibrary library = CreateLibrary(folder.Path, Hub());
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();
        BlockingProgress progress = new(started, release);

        Task<LocalModelDownloadResult> downloading = Task.Run(() => library.DownloadAsync("first", progress: progress, cancellationToken: TestContext.CancellationToken), TestContext.CancellationToken);
        Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(10), TestContext.CancellationToken));
        bool removedWhileDownloading = await library.DeleteAsync("first", cancellationToken: TestContext.CancellationToken);
        release.Set();
        LocalModelDownloadResult result = await downloading;

        Assert.IsFalse(removedWhileDownloading);
        Assert.AreEqual(LocalModelDownloadStatus.Success, result.Status);
        Assert.IsTrue(File.Exists(library.CheckpointPath(library.Find("first")!)));
        Assert.IsTrue(await library.DeleteAsync("first", cancellationToken: TestContext.CancellationToken));
    }

    /// <summary>
    /// Holds a download at its first progress report until released.
    /// </summary>
    private sealed class BlockingProgress(ManualResetEventSlim started, ManualResetEventSlim release) : IProgress<LocalModelDownloadProgress>
    {
        public void Report(LocalModelDownloadProgress value)
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        }
    }

    [TestMethod]
    public async Task MissingFile_MakesTheModelUnavailableUntilDownloadedAgain_WithoutDownloadingTheRest()
    {
        using TestFolder folder = new();
        FakeHub hub = Hub();
        ImageModelLibrary library = CreateLibrary(folder.Path, hub);
        await library.DownloadAsync("first", cancellationToken: TestContext.CancellationToken);
        File.Delete(Path.Combine(library.ModelDirectory(library.Find("first")!), "LICENSE.txt"));
        int requestsBefore = hub.Requested.Count;

        bool downloadedWithoutLicense = library.List().Single(model => model.Id == "first").IsDownloaded;
        ImageModel? activeWithoutLicense = library.List().FirstOrDefault(model => model.IsSelected);
        LocalModelDownloadResult restored = await library.DownloadAsync("first", cancellationToken: TestContext.CancellationToken);
        List<string> requested = hub.Requested.Skip(requestsBefore).ToList();

        Assert.IsFalse(downloadedWithoutLicense);
        Assert.IsNull(activeWithoutLicense);
        Assert.AreEqual(LocalModelDownloadStatus.Success, restored.Status);
        Assert.AreEqual("first", library.Active?.Id);
        Assert.AreSequenceEqual(new[] { $"{REPO}/resolve/{REVISION}/LICENSE.txt" }, requested);
    }

    internal static FakeHub Hub()
    {
        FakeHub hub = new();
        hub.Files[$"{REPO}/resolve/{REVISION}/first.gguf"] = FIRST;
        hub.Files[$"{REPO}/resolve/{REVISION}/second.gguf"] = SECOND;
        hub.Files[$"{REPO}/resolve/{REVISION}/LICENSE.txt"] = LICENSE;
        return hub;
    }

    private static ImageModelLibrary CreateLibrary(string folder, FakeHub hub)
    {
        return new ImageModelLibrary([Definition("first", FIRST), Definition("second", SECOND)], folder, hub, new ImageGenerationGate(), NullLogger<ImageModelLibrary>.Instance);
    }

    private static ImageModelDefinition Definition(string id, byte[] content)
    {
        return new ImageModelDefinition(id, id, "A test model.", "MIT", null, $"{FakeHub.SITE}{REPO}/resolve/{REVISION}/",
            [new LocalModelFile($"{id}.gguf", content.Length, FakeHub.Sha256(content)), new LocalModelFile("LICENSE.txt", LICENSE.Length, FakeHub.Sha256(LICENSE))],
            ImageModelCatalog.DREAMSHAPER_SETTINGS, true);
    }

    private sealed class SynchronousDownloadProgress : IProgress<LocalModelDownloadProgress>
    {
        private readonly List<LocalModelDownloadProgress> _updates;

        public SynchronousDownloadProgress(List<LocalModelDownloadProgress> updates)
        {
            _updates = updates;
        }

        public void Report(LocalModelDownloadProgress value)
        {
            _updates.Add(value);
        }
    }
}
