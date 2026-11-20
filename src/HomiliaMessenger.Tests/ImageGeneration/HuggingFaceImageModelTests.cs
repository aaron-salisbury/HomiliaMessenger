using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Integrations.ImageGeneration;
using HomiliaMessenger.Integrations.LocalModels;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.ImageGeneration;

/// <summary>
/// Adding image models from Hugging Face: what each GGUF file in a repository is, read from its header, and adding a
/// supported one with the settings the user gives.
/// </summary>
[TestClass]
public sealed class HuggingFaceImageModelTests
{
    private const string REPO = "someone/portrait-models";
    private const string REVISION = "fedcba9876543210fedcba9876543210fedcba98";
    private static readonly ImageGenerationSettings SETTINGS = new(512, 512, "euler_a", null, 20, 7f);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task LookUp_ReadsEachFilesHeaderToTellWhatItIs()
    {
        using TestFolder folder = new();
        FakeHub hub = new();
        byte[] sd1 = GgufTestFile.StableDiffusion1();
        hub.AddRepository(REPO, REVISION, "creativeml-openrail-m", "text-to-image",
            ("portrait-q8_0.gguf", sd1),
            ("unet-only.gguf", GgufTestFile.Write([], GgufTestFile.StableDiffusion(textEncoder: false, vae: false), padding: 200)),
            ("sdxl.gguf", GgufTestFile.Write([], GgufTestFile.StableDiffusion(contextWidth: 2048, classEmbedding: true), padding: 300)),
            ("chat-Q4_K_M.gguf", GgufTestFile.LanguageModel()),
            ("model.safetensors", Encoding.UTF8.GetBytes("not inspected")));
        ImageModelLibrary library = CreateLibrary(folder.Path, hub);

        ImageModelLookup lookup = await library.LookUpAsync($"https://huggingface.co/{REPO}/tree/main", cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(HuggingFaceLookupStatus.Found, lookup.Status);
        Assert.AreEqual(REVISION, lookup.Revision);
        Assert.AreEqual("creativeml-openrail-m", lookup.License);
        Assert.AreEqual("text-to-image", lookup.PipelineTag);
        Assert.HasCount(4, lookup.Files);

        ImageModelFile portrait = lookup.Files.Single(file => file.File.Path == "portrait-q8_0.gguf");
        Assert.IsTrue(portrait.IsSupported);
        Assert.AreEqual(ImageModelFamily.StableDiffusion1, portrait.Family);
        Assert.AreEqual("Q8_0", portrait.Quantization);
        Assert.AreEqual(sd1.Length, portrait.File.Bytes);
        Assert.AreEqual(FakeHub.Sha256(sd1), portrait.File.Sha256);

        Assert.Contains("only part of the model", lookup.Files.Single(file => file.File.Path == "unet-only.gguf").Problem!);
        Assert.AreEqual(ImageModelFamily.StableDiffusionXL, lookup.Files.Single(file => file.File.Path == "sdxl.gguf").Family);
        Assert.AreEqual(ImageModelFamily.LanguageModel, lookup.Files.Single(file => file.File.Path == "chat-Q4_K_M.gguf").Family);

        // Only headers were read, with range requests, and nothing but GGUF files.
        Assert.IsTrue(hub.Ranges.All(range => range.EndsWith($"bytes=0-{ImageModelLibrary.FIRST_HEADER_READ - 1}", StringComparison.Ordinal)
            || range.Contains("bytes=0-", StringComparison.Ordinal)));
        Assert.DoesNotContain(path => path.EndsWith("model.safetensors", StringComparison.Ordinal), hub.Requested);
    }

    [TestMethod]
    public async Task LookUp_ReadsMoreOfAHeaderLongerThanTheFirstRead()
    {
        using TestFolder folder = new();
        FakeHub hub = new();
        // Many tensors make a header longer than the first read.
        System.Collections.Generic.List<(string Name, long[] Shape, uint Type)> tensors = GgufTestFile.StableDiffusion();
        tensors.AddRange(Enumerable.Range(0, 6000).Select(index => ($"model.diffusion_model.output_blocks.{index}.0.in_layers.0.weight_with_a_long_name_to_fill_space", new long[] { 1280 }, GgufTestFile.F16)));
        byte[] large = GgufTestFile.Write([], tensors);
        Assert.IsGreaterThan(ImageModelLibrary.FIRST_HEADER_READ, large.Length - 64);
        hub.AddRepository(REPO, REVISION, null, null, ("large.gguf", large));

        ImageModelLookup lookup = await CreateLibrary(folder.Path, hub).LookUpAsync(REPO, cancellationToken: TestContext.CancellationToken);

        Assert.IsTrue(lookup.Files.Single().IsSupported, lookup.Files.Single().Problem);
        Assert.HasCount(2, hub.Ranges);
    }

    [TestMethod]
    public async Task LookUp_RecognizesTheBuiltInModelByItsHash()
    {
        using TestFolder folder = new();
        FakeHub hub = new();
        byte[] content = GgufTestFile.StableDiffusion1();
        hub.AddRepository(REPO, REVISION, null, null, ("copy.gguf", content));
        ImageModelDefinition builtIn = new("built-in", "Built in", "A model.", "MIT", null, "https://example.test/",
            [new LocalModelFile("model.gguf", content.Length, FakeHub.Sha256(content))], SETTINGS, true);
        ImageModelLibrary library = new([builtIn], folder.Path, hub, new ImageGenerationGate(), NullLogger<ImageModelLibrary>.Instance);

        ImageModelLookup lookup = await library.LookUpAsync(REPO, cancellationToken: TestContext.CancellationToken);
        LocalModelAddResult added = await library.AddAsync(lookup, lookup.Files.Single(), SETTINGS, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("built-in", lookup.Files.Single().KnownModelId);
        Assert.IsFalse(lookup.Files.Single().IsSupported);
        Assert.AreEqual("built-in", added.ModelId);
        Assert.HasCount(1, library.List());
    }

    [TestMethod]
    public async Task LookUp_ExplainsRepositoriesItCannotUse()
    {
        using TestFolder folder = new();
        FakeHub hub = new();
        hub.AddRepository("someone/safetensors-only", REVISION, null, null, ("model.safetensors", [1, 2, 3]));
        hub.Unreachable.Add("api/models/someone/offline");
        ImageModelLibrary library = CreateLibrary(folder.Path, hub);

        ImageModelLookup noGguf = await library.LookUpAsync("someone/safetensors-only", cancellationToken: TestContext.CancellationToken);
        ImageModelLookup missing = await library.LookUpAsync("someone/missing", cancellationToken: TestContext.CancellationToken);
        ImageModelLookup offline = await library.LookUpAsync("someone/offline", cancellationToken: TestContext.CancellationToken);
        ImageModelLookup nonsense = await library.LookUpAsync("not a repository", cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(HuggingFaceLookupStatus.NoModels, noGguf.Status);
        Assert.Contains("no GGUF files", noGguf.Problem!);
        Assert.AreEqual(HuggingFaceLookupStatus.NotFound, missing.Status);
        Assert.AreEqual(HuggingFaceLookupStatus.Failed, offline.Status);
        Assert.Contains("Could not reach Hugging Face", offline.Problem!);
        Assert.AreEqual(HuggingFaceLookupStatus.NotFound, nonsense.Status);
    }

    [TestMethod]
    public async Task LookUp_WhenAHeaderCannotBeRead_MarksThatFileOnly()
    {
        using TestFolder folder = new();
        FakeHub hub = new();
        hub.AddRepository(REPO, REVISION, null, null, ("good.gguf", GgufTestFile.StableDiffusion1()), ("unreadable.gguf", GgufTestFile.Write([], GgufTestFile.StableDiffusion(), padding: 500)));
        hub.Unreachable.Add($"{REPO}/resolve/{REVISION}/unreadable.gguf");

        ImageModelLookup lookup = await CreateLibrary(folder.Path, hub).LookUpAsync(REPO, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(HuggingFaceLookupStatus.Found, lookup.Status);
        Assert.IsTrue(lookup.Files.Single(file => file.File.Path == "good.gguf").IsSupported);
        Assert.Contains("Could not read the file's header", lookup.Files.Single(file => file.File.Path == "unreadable.gguf").Problem!);
    }

    [TestMethod]
    public async Task Add_KeepsTheUsersSettingsPinsTheRevisionAndIsUnverified()
    {
        using TestFolder folder = new();
        FakeHub hub = new();
        byte[] sd1 = GgufTestFile.StableDiffusion1();
        hub.AddRepository(REPO, REVISION, "creativeml-openrail-m", "text-to-image", ("portrait-q8_0.gguf", sd1));
        ImageModelLibrary library = CreateLibrary(folder.Path, hub);
        ImageModelLookup lookup = await library.LookUpAsync(REPO, cancellationToken: TestContext.CancellationToken);
        ImageGenerationSettings lcm = new(512, 512, "lcm", null, 4, 1f);

        LocalModelAddResult added = await library.AddAsync(lookup, lookup.Files.Single(), lcm, cancellationToken: TestContext.CancellationToken);
        LocalModelAddResult again = await library.AddAsync(lookup, lookup.Files.Single(), lcm, cancellationToken: TestContext.CancellationToken);
        ImageModelLibrary restarted = CreateLibrary(folder.Path, hub);
        ImageModelDefinition model = restarted.Find(added.ModelId)!;
        LocalModelDownloadResult download = await restarted.DownloadAsync(model.Id, cancellationToken: TestContext.CancellationToken);

        Assert.IsTrue(added.IsSuccessful);
        Assert.AreEqual(added.ModelId, again.ModelId);
        Assert.AreEqual(lcm, model.Settings);
        Assert.IsFalse(model.IsVerified);
        Assert.IsTrue(model.IsUserAdded);
        Assert.AreEqual("creativeml-openrail-m", model.License);
        Assert.AreEqual($"{FakeHub.SITE}{REPO}/resolve/{REVISION}/", model.BaseUrl);
        Assert.AreEqual(LocalModelDownloadStatus.Success, download.Status);
        Assert.AreEqual(model.Id, restarted.Active?.Id);

        Assert.IsTrue(await restarted.DeleteAsync(model.Id, cancellationToken: TestContext.CancellationToken));
        Assert.IsNull(CreateLibrary(folder.Path, hub).Find(model.Id));
    }

    [TestMethod]
    public async Task Add_RefusesUnsupportedFilesAndUnusableSettings()
    {
        using TestFolder folder = new();
        FakeHub hub = new();
        hub.AddRepository(REPO, REVISION, null, null, ("chat.gguf", GgufTestFile.LanguageModel()), ("portrait.gguf", GgufTestFile.StableDiffusion1()));
        ImageModelLibrary library = CreateLibrary(folder.Path, hub);
        ImageModelLookup lookup = await library.LookUpAsync(REPO, cancellationToken: TestContext.CancellationToken);

        LocalModelAddResult languageModel = await library.AddAsync(lookup, lookup.Files.Single(file => file.File.Path == "chat.gguf"), SETTINGS, cancellationToken: TestContext.CancellationToken);
        LocalModelAddResult badSettings = await library.AddAsync(lookup, lookup.Files.Single(file => file.File.Path == "portrait.gguf"), SETTINGS with { Steps = 0 }, cancellationToken: TestContext.CancellationToken);
        LocalModelAddResult stale = await library.AddAsync(lookup with { Revision = null }, lookup.Files.Single(file => file.File.Path == "portrait.gguf"), SETTINGS, cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(languageModel.IsSuccessful);
        Assert.Contains("language model", languageModel.Problem!);
        Assert.IsFalse(badSettings.IsSuccessful);
        Assert.Contains("Steps", badSettings.Problem!);
        Assert.IsFalse(stale.IsSuccessful);
        Assert.IsEmpty(library.List());
    }

    [TestMethod]
    public async Task Dialog_OffersOnlySupportedFilesAndRequiresAcknowledgingTheyAreUnverified()
    {
        using TestFolder folder = new();
        FakeHub hub = new();
        hub.AddRepository(REPO, REVISION, "creativeml-openrail-m", "text-to-image",
            ("chat.gguf", GgufTestFile.LanguageModel()), ("portrait-lcm.gguf", GgufTestFile.StableDiffusion1()));
        ImageModelLibrary library = CreateLibrary(folder.Path, hub);
        AddImageModelViewModel dialog = new(library) { Repository = REPO };
        string? added = null;
        dialog.Added += (_, _) => added = dialog.AddedModelId;

        await dialog.LookUpCommand.ExecuteAsync(null);
        bool canAddBeforeAcknowledging = dialog.AddCommand.CanExecute(null);
        dialog.AcceptsUnverified = true;
        bool canAddAfter = dialog.AddCommand.CanExecute(null);
        dialog.Steps = null;
        bool canAddWithoutSteps = dialog.AddCommand.CanExecute(null);
        dialog.Steps = 6;
        dialog.Sampler = "lcm";
        dialog.GuidanceScale = 1.5m;
        await dialog.AddCommand.ExecuteAsync(null);

        Assert.HasCount(2, dialog.Files);
        Assert.AreEqual("portrait-lcm.gguf", dialog.SelectedFile!.Name);
        Assert.Contains("lcm", dialog.SuggestionHint!);
        Assert.AreEqual(AddImageModelViewModel.DEFAULT_SAMPLER, ImageGenerationSettings.SAMPLERS[0]);
        Assert.IsTrue(dialog.Files.Single(file => file.Name == "chat.gguf").HasNote);
        Assert.IsFalse(canAddBeforeAcknowledging);
        Assert.IsTrue(canAddAfter);
        Assert.IsFalse(canAddWithoutSteps);
        Assert.IsNotNull(added);
        Assert.AreEqual(new ImageGenerationSettings(512, 512, "lcm", null, 6, 1.5f), library.Find(added)!.Settings);
    }

    private static ImageModelLibrary CreateLibrary(string folder, FakeHub hub)
    {
        return new ImageModelLibrary([], folder, hub, new ImageGenerationGate(), NullLogger<ImageModelLibrary>.Instance);
    }
}
