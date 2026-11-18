using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Integrations.ImageGeneration;
using HomiliaMessenger.Integrations.LocalModels;
using HomiliaMessenger.Presentation.Desktop.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.ImageGeneration;

/// <summary>
/// Selfie models: their own list and choice, face matching's download and preparation, drawing with a face, and the
/// prompt a selfie becomes.
/// </summary>
[TestClass]
public sealed class SelfieImageTests
{
    private const string REPO = "test/faces";
    private const string REVISION = "fedcba9876543210fedcba9876543210fedcba98";
    private static readonly byte[] CHECKPOINT = Encoding.UTF8.GetBytes("selfie-capable image model weights");
    private static readonly byte[] ADAPTER = Encoding.UTF8.GetBytes("ip-adapter plus-face weights");
    private static readonly byte[] ENCODER = Encoding.UTF8.GetBytes("full precision image encoder weights, rather long");
    private const long PREPARED_BYTES = 17;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Catalog_OffersDreamShaperForSelfiesWithTheExperimentsSettingsAndPinnedFaceMatching()
    {
        ImageModelDefinition model = ImageModelCatalog.Models.Single();

        Assert.IsTrue(model.IsOffered(ImageModelPurpose.Portraits));
        Assert.IsTrue(model.IsOffered(ImageModelPurpose.Selfies));
        Assert.AreEqual(new ImageGenerationSettings(512, 640, "lcm", null, 4, 1.5f), model.SettingsFor(ImageModelPurpose.Selfies));
        Assert.AreEqual(ImageModelCatalog.DREAMSHAPER_SETTINGS, model.SettingsFor(ImageModelPurpose.Portraits));
        Assert.Contains("face matching", model.SelfieDescription!);
        Assert.Contains("2.6 GB", model.SelfieDescription!);
        Assert.StartsWith("https://huggingface.co/h94/IP-Adapter/resolve/018e402774aeeddd60609b4ecdb7e298259dc729/", FaceMatchingCatalog.BASE_URL);
        Assert.AreEqual(98_183_288L, FaceMatchingCatalog.ADAPTER.Size);
        Assert.AreEqual(2_528_373_448L, FaceMatchingCatalog.IMAGE_ENCODER.Size);
        Assert.AreEqual(0.3f, FaceMatchingCatalog.STRENGTH);
    }

    [TestMethod]
    public async Task Selfies_NeedFaceMatchingBesidesAModelDownloadedForPhotos_AndDownloadIt()
    {
        using TestFolder folder = new();
        FakeHub hub = Hub();
        FakeBackend backend = new() { ConvertedBytes = PREPARED_BYTES };
        ImageModelLibrary library = CreateLibrary(folder.Path, hub, backend);

        // Downloaded for contact photos only: offered for selfies, chosen there by default, but not ready without face matching.
        Assert.AreEqual(LocalModelDownloadStatus.Success, (await library.DownloadAsync("both", cancellationToken: TestContext.CancellationToken)).Status);
        ImageModel forSelfies = library.List(ImageModelPurpose.Selfies).Single(model => model.Id == "both");

        Assert.IsFalse(forSelfies.IsDownloaded);
        Assert.AreEqual(CHECKPOINT.Length + ADAPTER.Length + ENCODER.Length, forSelfies.DownloadBytes);
        Assert.AreEqual("both", library.ActiveFor(ImageModelPurpose.Selfies)?.Id);
        Assert.IsFalse(library.GetFaceMatching().IsDownloaded);

        List<LocalModelDownloadProgress> progress = [];
        LocalModelDownloadResult result = await library.DownloadAsync("both", ImageModelPurpose.Selfies, new ListProgress(progress), TestContext.CancellationToken);

        Assert.AreEqual(LocalModelDownloadStatus.Success, result.Status, result.Problem);
        Assert.IsTrue(library.List(ImageModelPurpose.Selfies).Single(model => model.Id == "both").IsDownloaded);
        Assert.IsTrue(library.GetFaceMatching().IsDownloaded);
        Assert.AreEqual(PREPARED_BYTES + ADAPTER.Length, library.GetFaceMatching().DiskBytes);

        // The encoder was converted in place, and the downloaded one removed; the checkpoint was not downloaded again.
        (string input, string output) = backend.Conversions.Single();
        Assert.AreEqual(Path.Combine(library.FaceMatchingDirectory, "model.safetensors"), input);
        Assert.EndsWith(".tmp", output);
        Assert.IsFalse(File.Exists(input));
        Assert.IsTrue(File.Exists(library.FaceMatchingPaths.ImageEncoderPath));
        Assert.ContainsSingle(path => path.EndsWith("both.gguf", StringComparison.Ordinal), hub.Requested);
        Assert.AreEqual(progress[^1].TotalBytes, progress[^1].BytesDownloaded);
        Assert.AreEqual(ADAPTER.Length + ENCODER.Length, progress[^1].TotalBytes);
    }

    [TestMethod]
    public async Task FaceMatching_WithoutTheRuntime_IsNotDownloaded()
    {
        using TestFolder folder = new();
        FakeHub hub = Hub();
        ImageModelLibrary library = CreateLibrary(folder.Path, hub, new FakeBackend { RuntimeProblem = "Not included." });

        LocalModelDownloadResult result = await library.DownloadAsync("both", ImageModelPurpose.Selfies, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(LocalModelDownloadStatus.Failed, result.Status);
        Assert.AreEqual("Not included.", result.Problem);
        Assert.DoesNotContain(path => path.Contains("model.safetensors", StringComparison.Ordinal), hub.Requested);
    }

    [TestMethod]
    public async Task FaceMatching_InterruptedBeforeItsEncoderWasPrepared_IsPreparedWithoutDownloadingAgain()
    {
        using TestFolder folder = new();
        FakeHub hub = Hub();
        FakeBackend backend = new() { ConvertedBytes = PREPARED_BYTES };
        ImageModelLibrary library = CreateLibrary(folder.Path, hub, backend);
        Directory.CreateDirectory(library.FaceMatchingDirectory);
        File.WriteAllBytes(Path.Combine(library.FaceMatchingDirectory, "model.safetensors"), ENCODER);
        File.WriteAllBytes(library.FaceMatchingPaths.AdapterPath, ADAPTER);

        LocalModelDownloadResult result = await library.DownloadFaceMatchingAsync(null, TestContext.CancellationToken);

        Assert.AreEqual(LocalModelDownloadStatus.Success, result.Status, result.Problem);
        Assert.IsEmpty(hub.Requested);
        Assert.HasCount(1, backend.Conversions);
        Assert.IsTrue(library.IsFaceMatchingReady);
    }

    [TestMethod]
    public async Task FaceMatching_PausedWhileItsEncoderIsPrepared_IsNotReady_AndResumesWithoutDownloadingAgain()
    {
        using TestFolder folder = new();
        FakeHub hub = Hub();
        using CancellationTokenSource pause = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        FakeBackend backend = new() { ConvertedBytes = PREPARED_BYTES, Converting = pause.Cancel };
        ImageModelLibrary library = CreateLibrary(folder.Path, hub, backend);

        LocalModelDownloadResult paused = await library.DownloadFaceMatchingAsync(null, pause.Token);
        int requested = hub.Requested.Count;
        backend.Converting = null;
        LocalModelDownloadResult resumed = await library.DownloadFaceMatchingAsync(null, TestContext.CancellationToken);

        Assert.AreEqual(LocalModelDownloadStatus.Canceled, paused.Status);
        Assert.AreEqual(LocalModelDownloadStatus.Success, resumed.Status, resumed.Problem);
        Assert.HasCount(requested, hub.Requested);
        Assert.HasCount(2, backend.Conversions);
        Assert.IsTrue(library.IsFaceMatchingReady);
    }

    [TestMethod]
    public async Task FaceMatching_WithOneFileMissing_KeepsTheOtherAndDownloadsOnlyWhatIsMissing()
    {
        using TestFolder folder = new();
        FakeHub hub = Hub();
        ImageModelLibrary library = CreateLibrary(folder.Path, hub, new FakeBackend { ConvertedBytes = PREPARED_BYTES });
        Directory.CreateDirectory(library.FaceMatchingDirectory);
        File.WriteAllBytes(library.FaceMatchingPaths.AdapterPath, ADAPTER);

        LocalModelDownloadResult result = await library.DownloadFaceMatchingAsync(null, TestContext.CancellationToken);

        Assert.AreEqual(LocalModelDownloadStatus.Success, result.Status, result.Problem);
        Assert.IsTrue(library.IsFaceMatchingReady);
        Assert.DoesNotContain(path => path.EndsWith("ip-adapter-plus-face_sd15.safetensors", StringComparison.Ordinal), hub.Requested, "The adapter already in place is kept.");
        Assert.Contains(path => path.EndsWith("image_encoder/model.safetensors", StringComparison.Ordinal), hub.Requested);
    }

    [TestMethod]
    public async Task FaceMatching_MissingOnlyItsAdapter_DownloadsTheAdapterAndKeepsThePreparedEncoder()
    {
        using TestFolder folder = new();
        FakeHub hub = Hub();
        FakeBackend backend = new() { ConvertedBytes = PREPARED_BYTES };
        ImageModelLibrary library = CreateLibrary(folder.Path, hub, backend);
        Directory.CreateDirectory(library.FaceMatchingDirectory);
        File.WriteAllBytes(library.FaceMatchingPaths.ImageEncoderPath, new byte[PREPARED_BYTES]);

        LocalModelDownloadResult result = await library.DownloadFaceMatchingAsync(null, TestContext.CancellationToken);

        Assert.AreEqual(LocalModelDownloadStatus.Success, result.Status, result.Problem);
        Assert.IsTrue(library.IsFaceMatchingReady);
        Assert.IsEmpty(backend.Conversions);
        Assert.DoesNotContain(path => path.EndsWith("image_encoder/model.safetensors", StringComparison.Ordinal), hub.Requested);
    }

    [TestMethod]
    public async Task FaceMatching_PreparedAtTheWrongSize_IsDiscardedAndReported()
    {
        using TestFolder folder = new();
        ImageModelLibrary library = CreateLibrary(folder.Path, Hub(), new FakeBackend { ConvertedBytes = PREPARED_BYTES + 1 });

        LocalModelDownloadResult result = await library.DownloadFaceMatchingAsync(null, TestContext.CancellationToken);

        Assert.AreEqual(LocalModelDownloadStatus.Failed, result.Status);
        Assert.IsFalse(library.IsFaceMatchingReady);
        Assert.IsEmpty(Directory.EnumerateFiles(library.FaceMatchingDirectory, "*.tmp"));
    }

    [TestMethod]
    public async Task SelectingAndRemoving_ForSelfies_LeaveContactPhotosAlone()
    {
        using TestFolder folder = new();
        FakeBackend backend = new() { ConvertedBytes = PREPARED_BYTES };
        ImageModelLibrary library = CreateLibrary(folder.Path, Hub(), backend);
        await library.DownloadAsync("both", ImageModelPurpose.Selfies, cancellationToken: TestContext.CancellationToken);
        await library.DownloadAsync("photos", cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(library.Select("photos", ImageModelPurpose.Selfies), "A model offered only for photos cannot be chosen for selfies.");
        Assert.IsTrue(library.Select("photos"));
        Assert.AreEqual("photos", library.Active?.Id);
        Assert.AreEqual("both", library.ActiveFor(ImageModelPurpose.Selfies)?.Id);
        Assert.DoesNotContain(model => model.Id == "photos", library.List(ImageModelPurpose.Selfies));

        // Removing the shared model from selfies removes face matching only; the model still makes photos.
        Assert.IsTrue(await library.DeleteAsync("both", ImageModelPurpose.Selfies, TestContext.CancellationToken));

        Assert.IsFalse(library.GetFaceMatching().IsDownloaded);
        Assert.IsFalse(Directory.Exists(library.FaceMatchingDirectory));
        Assert.IsTrue(library.List().Single(model => model.Id == "both").IsDownloaded);
        Assert.IsFalse(library.List(ImageModelPurpose.Selfies).Single(model => model.Id == "both").IsDownloaded);
    }

    [TestMethod]
    public async Task RemovingTheBuiltInSharedModelFromPhotos_RemovesIt_AfterWarningThatSelfiesStopToo()
    {
        using TestFolder folder = new();
        ImageModelLibrary library = CreateLibrary(folder.Path, Hub(), new FakeBackend { ConvertedBytes = PREPARED_BYTES });
        await library.DownloadAsync("both", ImageModelPurpose.Selfies, cancellationToken: TestContext.CancellationToken);
        string? asked = null;
        ImageModelItem item = new(library.List().Single(model => model.Id == "both"), library, (_, question) =>
        {
            asked = question;
            return Task.FromResult(true);
        }, () => { }, ImageModelPurpose.Portraits);

        await item.DeleteCommand.ExecuteAsync(null);

        Assert.Contains("It also takes selfies", asked!);
        Assert.IsFalse(library.List().Single(model => model.Id == "both").IsDownloaded);
        Assert.IsFalse(library.List(ImageModelPurpose.Selfies).Single(model => model.Id == "both").IsDownloaded);
        Assert.IsTrue(library.GetFaceMatching().IsDownloaded, "Face matching stays for other selfie models.");
    }

    [TestMethod]
    public async Task AddingForSelfies_DrawsSelfiesAtTheSelfieSize_AndAddingForPhotosToo_KeepsBothSettings()
    {
        using TestFolder folder = new();
        FakeHub hub = new();
        byte[] checkpoint = GgufTestFile.StableDiffusion1();
        hub.AddRepository("someone/sd15", REVISION, "openrail", "text-to-image", ("portrait.gguf", checkpoint));
        ImageModelLibrary library = new([], folder.Path, hub, new ImageGenerationGate(), NullLogger<ImageModelLibrary>.Instance, backend: new FakeBackend());
        ImageModelLookup lookup = await library.LookUpAsync("someone/sd15", TestContext.CancellationToken);
        ImageGenerationSettings selfie = new(ImageGenerationSettings.SELFIE_WIDTH, ImageGenerationSettings.SELFIE_HEIGHT, "euler_a", null, 20, 7f);
        ImageGenerationSettings photo = new(512, 512, "euler_a", null, 25, 6f);

        LocalModelAddResult added = await library.AddAsync(lookup, lookup.Files.Single(), selfie, ImageModelPurpose.Selfies, TestContext.CancellationToken);

        Assert.IsTrue(added.IsSuccessful, added.Problem);
        Assert.IsEmpty(library.List());
        Assert.AreEqual(selfie, library.List(ImageModelPurpose.Selfies).Single().Settings);
        Assert.Contains("face matching", library.List(ImageModelPurpose.Selfies).Single().Description);

        LocalModelAddResult again = await library.AddAsync(lookup, lookup.Files.Single(), photo, ImageModelPurpose.Portraits, TestContext.CancellationToken);
        ImageModelLibrary restarted = new([], folder.Path, hub, new ImageGenerationGate(), NullLogger<ImageModelLibrary>.Instance);

        Assert.AreEqual(added.ModelId, again.ModelId);
        Assert.AreEqual(photo, restarted.List().Single().Settings);
        Assert.AreEqual(selfie, restarted.List(ImageModelPurpose.Selfies).Single().Settings);

        // Removed from photos, it stays offered for selfies, and its checkpoint stays downloaded for them.
        Assert.AreEqual(LocalModelDownloadStatus.Success, (await restarted.DownloadAsync(added.ModelId!, cancellationToken: TestContext.CancellationToken)).Status);
        Assert.IsTrue(await restarted.DeleteAsync(added.ModelId!, cancellationToken: TestContext.CancellationToken));
        Assert.IsEmpty(restarted.List());
        Assert.HasCount(1, restarted.List(ImageModelPurpose.Selfies));
        Assert.IsTrue(File.Exists(restarted.CheckpointPath(restarted.Find(added.ModelId)!)));
        Assert.IsTrue(restarted.IsDownloaded(restarted.Find(added.ModelId)!));
    }

    [TestMethod]
    public async Task Generator_DrawsASelfieWithTheFaceTheModelsSelfieSettingsAndFaceMatching()
    {
        using TestFolder folder = new();
        FakeBackend backend = new() { ConvertedBytes = PREPARED_BYTES };
        ImageGenerationGate gate = new();
        ImageModelLibrary library = CreateLibrary(folder.Path, Hub(), backend, gate);
        StableDiffusionImageGenerator generator = new(library, backend, gate, NullLogger<StableDiffusionImageGenerator>.Instance);
        ISelfieImageGenerator selfies = generator;

        Assert.IsFalse(selfies.GetAvailability().IsReady);
        Assert.Contains("Settings", selfies.GetAvailability().Problem!);

        await library.DownloadAsync("both", ImageModelPurpose.Selfies, cancellationToken: TestContext.CancellationToken);
        SelfieGeneratorAvailability availability = selfies.GetAvailability();
        SelfieSubject walt = new("Walt", FakeImageGenerator.TinyPng(90), "older man with a white beard", "irritated");
        SelfieSubject elena = new("Elena", FakeImageGenerator.TinyPng(30), "woman in her thirties");

        ImageGenerationResult result = await generator.GenerateAsync(new SelfieImageRequest([walt, elena], new SelfieScene("a bus stop"), 77), cancellationToken: TestContext.CancellationToken);

        Assert.IsTrue(availability.IsReady, availability.Problem);
        Assert.AreEqual(1, availability.MaxSubjects);
        Assert.AreEqual(ImageGenerationStatus.Success, result.Status, result.Problem);
        (Drawing drawing, FaceMatchingFiles? faceMatching) = backend.Drawings.Single();
        Assert.AreEqual(library.FaceMatchingPaths, faceMatching);
        Assert.AreEqual(77, drawing.Seed);
        Assert.AreEqual(FaceMatchingCatalog.STRENGTH, drawing.FaceStrength);
        Assert.AreEqual(512, drawing.Face!.Width);
        Assert.HasCount(512 * 512 * 3, drawing.Face.Pixels);
        Assert.AreEqual(90, drawing.Face.Pixels[0], "Only the first subject's face is drawn.");
        Assert.Contains("older man with a white beard", drawing.Prompt);
        Assert.DoesNotContain("woman", drawing.Prompt);
        Assert.Contains("happy", drawing.NegativePrompt!);
        Assert.AreEqual(640, backend.Requests.Single().Settings.Height);
        (int width, int height, _) = PngTestDecoder.Decode(result.Png!);
        Assert.AreEqual((512, 640), (width, height));
    }

    [TestMethod]
    public async Task Generator_WithAPhotoItCannotRead_SaysWhoseRatherThanDrawing()
    {
        using TestFolder folder = new();
        FakeBackend backend = new() { ConvertedBytes = PREPARED_BYTES };
        ImageGenerationGate gate = new();
        ImageModelLibrary library = CreateLibrary(folder.Path, Hub(), backend, gate);
        await library.DownloadAsync("both", ImageModelPurpose.Selfies, cancellationToken: TestContext.CancellationToken);
        StableDiffusionImageGenerator generator = new(library, backend, gate, NullLogger<StableDiffusionImageGenerator>.Instance);

        ImageGenerationResult result = await generator.GenerateAsync(new SelfieImageRequest([new SelfieSubject("Walt", [1, 2, 3], "man")], SelfieScene.Unknown, 1), cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(ImageGenerationStatus.Failed, result.Status);
        Assert.Contains("Walt", result.Problem!);
        Assert.AreEqual(0, backend.Opened);
    }

    [TestMethod]
    public void Prompt_PutsTheSelfieFramingFirst_AndDrawsTheExpressionAsTheExperimentFoundBest()
    {
        SelfiePrompt prompt = SelfiePromptComposer.Compose(
            new SelfieSubject("Marcus", [], "man in his fifties", "irritated", "a gray suit"),
            new SelfieScene("an airport gate", "waiting for a delayed flight", null, null, "evening"));

        Assert.StartsWith(SelfiePromptComposer.FRAMING, prompt.Prompt);
        Assert.Contains("(irritated:1.4), (annoyed scowl:1.4), (furrowed brow:1.3) man in his fifties", prompt.Prompt);
        Assert.Contains("wearing (a gray suit:1.4)", prompt.Prompt);
        Assert.Contains("Waiting for a delayed flight.", prompt.Prompt);
        Assert.Contains("An airport gate, evening.", prompt.Prompt);
        Assert.AreEqual("happy, smiling, calm, deformed, blurry", prompt.NegativePrompt);
    }

    [TestMethod]
    public void Prompt_SoaksThePersonInRain_ShowsPropsBesideTheFace_AndGesturesEmbarrassment()
    {
        SelfiePrompt rain = SelfiePromptComposer.Compose(new SelfieSubject("A", [], "woman with short gray hair", "laughing"), new SelfieScene(Weather: "pouring rain"));
        SelfiePrompt fish = SelfiePromptComposer.Compose(new SelfieSubject("B", [], "man", "proud"), new SelfieScene(Props: "a large trout"));
        SelfiePrompt shy = SelfiePromptComposer.Compose(new SelfieSubject("C", [], "woman", "a bit embarrassed"), SelfieScene.Unknown);

        Assert.Contains("(soaking wet:1.5)", rain.Prompt);
        Assert.Contains("(pouring rain:1.4), rain streaks", rain.Prompt);
        Assert.Contains("dry hair, dry clothes", rain.NegativePrompt);
        Assert.Contains("(a large trout held up beside the face:1.5)", fish.Prompt);
        Assert.Contains("(huge grin:1.3)", fish.Prompt);
        Assert.Contains("(covering half the face with one hand:1.4)", shy.Prompt);
    }

    [TestMethod]
    public void Prompt_KeepsWordsFromTheConversationOutOfTheEmphasisSyntax()
    {
        SelfiePrompt prompt = SelfiePromptComposer.Compose(
            new SelfieSubject("A", [], "man (tall):2", "giddy"),
            new SelfieScene("[the] {lab}: room"));

        Assert.Contains("(giddy:1.3) man tall 2", prompt.Prompt);
        Assert.Contains("The lab room.", prompt.Prompt);
        Assert.AreEqual("deformed, blurry", prompt.NegativePrompt);
    }

    [TestMethod]
    public void Prompt_WithoutAnExpression_SmilesForTheCamera()
    {
        SelfiePrompt prompt = SelfiePromptComposer.Compose(new SelfieSubject("A", [], " "), SelfieScene.Unknown);

        Assert.AreEqual($"{SelfiePromptComposer.FRAMING} (smiling:1.2) person.", prompt.Prompt);
        Assert.AreEqual("sad, deformed, blurry", prompt.NegativePrompt);
    }

    private static FakeHub Hub()
    {
        FakeHub hub = new();
        hub.Files[$"{REPO}/resolve/{REVISION}/both.gguf"] = CHECKPOINT;
        hub.Files[$"{REPO}/resolve/{REVISION}/photos.gguf"] = CHECKPOINT;
        hub.Files[$"{REPO}/resolve/{REVISION}/models/ip-adapter-plus-face_sd15.safetensors"] = ADAPTER;
        hub.Files[$"{REPO}/resolve/{REVISION}/models/image_encoder/model.safetensors"] = ENCODER;
        return hub;
    }

    private static ImageModelLibrary CreateLibrary(string folder, FakeHub hub, FakeBackend backend, ImageGenerationGate? gate = null)
    {
        string baseUrl = $"{FakeHub.SITE}{REPO}/resolve/{REVISION}/";
        LocalModelFile checkpoint(string name)
        {
            return new(name, CHECKPOINT.Length, FakeHub.Sha256(CHECKPOINT));
        }

        ImageModelDefinition both = new("both", "Both", "Photos and selfies.", "MIT", null, baseUrl, [checkpoint("both.gguf")],
            ImageModelCatalog.DREAMSHAPER_SETTINGS, true, Purposes: ImageModelPurpose.Portraits | ImageModelPurpose.Selfies, SelfieSettings: ImageModelCatalog.DREAMSHAPER_SELFIE_SETTINGS);
        ImageModelDefinition photos = new("photos", "Photos", "Photos only.", "MIT", null, baseUrl, [checkpoint("photos.gguf")], ImageModelCatalog.DREAMSHAPER_SETTINGS, true);
        FaceMatchingDefinition faceMatching = new(baseUrl,
            new LocalModelFile("models/ip-adapter-plus-face_sd15.safetensors", ADAPTER.Length, FakeHub.Sha256(ADAPTER)),
            new LocalModelFile("models/image_encoder/model.safetensors", ENCODER.Length, FakeHub.Sha256(ENCODER)),
            PREPARED_BYTES);

        return new ImageModelLibrary([both, photos], folder, hub, gate ?? new ImageGenerationGate(), NullLogger<ImageModelLibrary>.Instance, backend: backend, faceMatching: faceMatching);
    }

    private sealed class ListProgress : IProgress<LocalModelDownloadProgress>
    {
        private readonly List<LocalModelDownloadProgress> _updates;

        public ListProgress(List<LocalModelDownloadProgress> updates)
        {
            _updates = updates;
        }

        public void Report(LocalModelDownloadProgress value)
        {
            _updates.Add(value);
        }
    }
}
