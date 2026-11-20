using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Integrations.ImageGeneration;
using HomiliaMessenger.Integrations.LocalModels;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
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
/// Graphics card models: the built-in FLUX.2 [klein] 4B and DreamShaper XL Lightning, shared text encoder and VAE files,
/// drawing on the preferred card, selfies from a reference image in plain sentences, and adding SDXL and FLUX.2 [klein]
/// models from Hugging Face for a graphics card only.
/// </summary>
[TestClass]
public sealed class GpuImageGenerationTests
{
    private const string REPO = "test/klein";
    private const string REVISION = "fedcba9876543210fedcba9876543210fedcba98";
    private static readonly byte[] KLEIN = Encoding.UTF8.GetBytes("klein diffusion model weights");
    private static readonly byte[] TUNED = Encoding.UTF8.GetBytes("a fine-tuned klein diffusion model, a little longer");
    private static readonly byte[] TEXT_ENCODER = Encoding.UTF8.GetBytes("qwen3 text encoder weights");
    private static readonly byte[] VAE = Encoding.UTF8.GetBytes("flux2 vae weights");
    private static readonly ImageGpuDevice CARD = new("CUDA0", "NVIDIA GeForce RTX 4070 Ti", 12_878_610_432, false);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Catalog_OffersKleinAndDreamShaperXlOnAGraphicsCard_PinnedAndVerified()
    {
        ImageModelDefinition klein = ImageModelCatalog.Models.Single(model => model.Id == ImageModelCatalog.FLUX2_KLEIN_4B);
        ImageModelDefinition xl = ImageModelCatalog.Models.Single(model => model.Id == ImageModelCatalog.DREAMSHAPER_XL_LIGHTNING);

        Assert.AreEqual(ImageProcessor.Gpu, klein.Processor);
        Assert.AreEqual(ImageModelFamily.Flux2Klein4B, klein.Family);
        Assert.AreEqual(ImagePromptStyle.Plain, klein.Style);
        Assert.AreEqual(SelfieFaceSource.ReferenceImage, klein.FaceSource);
        Assert.IsTrue(klein.IsDiffusionModelOnly);
        Assert.IsTrue(klein.IsVerified);
        Assert.AreEqual(ImageModelCatalog.APACHE_2, klein.License);
        Assert.AreEqual("flux-2-klein-4b-Q4_0.gguf", klein.Checkpoint.Name);
        Assert.AreEqual(64, klein.Checkpoint.Sha256.Length);
        Assert.AreSequenceEqual(new[] { ImageComponentRole.TextEncoder, ImageComponentRole.Vae }, klein.SharedFiles.Select(component => component.Role).ToArray());
        Assert.AreEqual(2_460_378_560L + 2_497_281_312L + 336_211_292L, klein.DownloadBytes);
        Assert.AreEqual(new ImageGenerationSettings(1024, 1024, "euler", null, 4, 1f), klein.SettingsFor(ImageModelPurpose.Portraits));
        Assert.AreEqual(new ImageGenerationSettings(896, 1152, "euler", null, 4, 1f), klein.SettingsFor(ImageModelPurpose.Selfies));
        Assert.IsTrue(klein.IsOffered(ImageModelPurpose.Selfies));

        Assert.AreEqual(ImageProcessor.Gpu, xl.Processor);
        Assert.AreEqual(ImageModelFamily.StableDiffusionXL, xl.Family);
        Assert.AreEqual(ImagePromptStyle.Weighted, xl.Style);
        Assert.AreEqual(SelfieFaceSource.FaceMatching, xl.FaceSource);
        Assert.IsFalse(xl.IsDiffusionModelOnly);
        Assert.IsEmpty(xl.SharedFiles);
        Assert.AreEqual(ImageModelCatalog.OPENRAIL_PLUS_PLUS_M, xl.License);
        Assert.AreEqual(new ImageGenerationSettings(1024, 1024, "euler_a", "sgm_uniform", 6, 2f), xl.Settings);
        Assert.IsNull(xl.Settings.Problem());
        Assert.IsNull(xl.SettingsFor(ImageModelPurpose.Selfies).Problem());
        Assert.IsNotNull(FaceMatchingCatalog.Definition.AdapterFor(ImageModelFamily.StableDiffusionXL));
        Assert.IsNull(FaceMatchingCatalog.Definition.AdapterFor(ImageModelFamily.Flux2Klein4B));
        Assert.AreEqual(0.5f, FaceMatchingCatalog.StrengthFor(ImageModelFamily.StableDiffusionXL));
    }

    [TestMethod]
    public void Settings_UseANegativePrompt_OnlyAboveCfgOne()
    {
        Assert.IsFalse(ImageModelCatalog.KLEIN_SETTINGS.SupportsNegativePrompt);
        Assert.IsTrue(ImageModelCatalog.DREAMSHAPER_XL_SETTINGS.SupportsNegativePrompt);
        Assert.IsFalse(ImageModelCatalog.DREAMSHAPER_SETTINGS.SupportsNegativePrompt);
        Assert.IsTrue(ImageModelCatalog.DREAMSHAPER_SELFIE_SETTINGS.SupportsNegativePrompt);
        Assert.AreEqual(ImagePromptStyle.Plain, IImageModelLibrary.PromptStyleOf(ImageModelFamily.Flux2Klein4B));
        Assert.AreEqual(ImagePromptStyle.Weighted, IImageModelLibrary.PromptStyleOf(ImageModelFamily.StableDiffusionXL));
    }

    [TestMethod]
    public async Task Download_FetchesSharedFilesOnce_AndRemovesThemWithTheLastModelThatUsesThem()
    {
        using TestFolder folder = new();
        FakeHub hub = Hub();
        ImageModelLibrary library = CreateLibrary(folder.Path, hub);

        LocalModelDownloadResult first = await library.DownloadAsync("klein", cancellationToken: TestContext.CancellationToken);
        int requestsAfterFirst = hub.Requested.Count;
        LocalModelDownloadResult second = await library.DownloadAsync("tuned", cancellationToken: TestContext.CancellationToken);
        List<string> secondRequests = hub.Requested.Skip(requestsAfterFirst).ToList();

        Assert.AreEqual(LocalModelDownloadStatus.Success, first.Status, first.Problem);
        Assert.AreEqual(LocalModelDownloadStatus.Success, second.Status, second.Problem);
        Assert.AreSequenceEqual(new[] { $"{REPO}/resolve/{REVISION}/tuned.gguf" }, secondRequests.ToArray(), "The shared files are downloaded once.");

        ImageModelDefinition klein = library.Find("klein")!;
        ImageModelPaths paths = library.PathsFor(klein);
        Assert.IsNull(paths.Checkpoint);
        Assert.AreSequenceEqual(KLEIN, File.ReadAllBytes(paths.DiffusionModel!));
        Assert.AreSequenceEqual(TEXT_ENCODER, File.ReadAllBytes(paths.TextEncoder!));
        Assert.AreSequenceEqual(VAE, File.ReadAllBytes(paths.Vae!));
        Assert.AreEqual(paths.TextEncoder, library.PathsFor(library.Find("tuned")!).TextEncoder);

        Assert.IsTrue(await library.DeleteAsync("klein", cancellationToken: TestContext.CancellationToken));
        Assert.IsTrue(File.Exists(paths.TextEncoder!), "Still used by the other model.");
        Assert.IsTrue(library.IsDownloaded(library.Find("tuned")!));

        Assert.IsTrue(await library.DeleteAsync("tuned", cancellationToken: TestContext.CancellationToken));
        Assert.IsFalse(File.Exists(paths.TextEncoder!));
        Assert.IsFalse(File.Exists(paths.Vae!));
    }

    [TestMethod]
    public async Task Selfies_ForAModelThatKeepsFacesFromAReference_NeedNoFaceMatching()
    {
        using TestFolder folder = new();
        ImageModelLibrary library = CreateLibrary(folder.Path, Hub());

        LocalModelDownloadResult result = await library.DownloadAsync("klein", ImageModelPurpose.Selfies, cancellationToken: TestContext.CancellationToken);
        ImageModel listed = library.List(ImageModelPurpose.Selfies).Single(model => model.Id == "klein");

        Assert.AreEqual(LocalModelDownloadStatus.Success, result.Status, result.Problem);
        Assert.IsTrue(listed.IsDownloaded);
        Assert.AreEqual(SelfieFaceSource.ReferenceImage, listed.FaceSource);
        Assert.AreEqual("MIT", listed.License, "No face matching license applies.");
        Assert.AreEqual(KLEIN.Length + TEXT_ENCODER.Length + VAE.Length, listed.DownloadBytes);
        Assert.IsFalse(Directory.Exists(library.FaceMatchingDirectory));
        Assert.IsFalse(await library.DeleteAsync("klein", ImageModelPurpose.Selfies, TestContext.CancellationToken), "There is nothing selfies alone need of it.");
        Assert.IsTrue(library.IsDownloaded(library.Find("klein")!));
    }

    [TestMethod]
    public async Task Generator_DrawsAGraphicsCardModelOnThePreferredCard_WithItsTextEncoderAndVae()
    {
        using TestFolder folder = new();
        FakeBackend backend = new();
        backend.Gpus.Add(CARD);
        backend.Gpus.Add(new ImageGpuDevice("Vulkan0", "NVIDIA GeForce RTX 4070 Ti", 12_878_610_432, false));
        (StableDiffusionImageGenerator generator, ImageModelLibrary library) = CreateGenerator(folder.Path, backend);

        ImageGenerationResult result = await generator.GenerateAsync(new ImageGenerationRequest("A woman in a red coat.", 5), cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(ImageGenerationStatus.Success, result.Status, result.Problem);
        ImageModelLoad load = backend.Loads.Single();
        Assert.AreEqual("CUDA0", load.Device);
        Assert.IsTrue(load.IsGpu);
        Assert.IsNull(load.Paths.Checkpoint);
        Assert.AreEqual(library.PathsFor(library.Find("klein")!), load.Paths);
        Assert.IsNull(load.FaceMatching);
        Assert.AreEqual(new ImageGenerationSettings(1024, 1024, "euler", null, 4, 1f), backend.Requests.Single().Settings);
    }

    [TestMethod]
    public async Task Generator_WithoutAGraphicsCard_SaysAGraphicsCardModelCannotRun()
    {
        using TestFolder folder = new();
        FakeBackend backend = new();
        (StableDiffusionImageGenerator generator, _) = CreateGenerator(folder.Path, backend);

        ImageGeneratorAvailability availability = generator.GetAvailability();
        ImageGenerationResult result = await generator.GenerateAsync(new ImageGenerationRequest("A woman.", 5), cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(availability.IsReady);
        Assert.IsTrue(availability.IsRuntimeIncluded);
        Assert.Contains("graphics card", availability.Problem!);
        Assert.AreEqual(ImageGenerationStatus.Unavailable, result.Status);
        Assert.AreEqual(0, backend.Opened);
    }

    [TestMethod]
    public async Task Generator_KleinSelfie_DrawsFromThePhotoAsAReference_InPlainSentences()
    {
        using TestFolder folder = new();
        FakeBackend backend = new();
        backend.Gpus.Add(CARD);
        (StableDiffusionImageGenerator generator, _) = CreateGenerator(folder.Path, backend);
        SelfieSubject walt = new("Walt", FakeImageGenerator.TinyPng(90), "older man with a white beard", "laughing", "a gray raincoat");
        SelfieImageRequest request = new([walt], new SelfieScene("a city bus stop", "waiting for the bus", null, "pouring rain", "gray daylight"), 7);

        SelfiePrompt prompt = generator.ComposePrompt(request);
        ImageGenerationResult result = await generator.GenerateAsync(request, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(ImagePromptStyle.Plain, prompt.Style);
        Assert.IsFalse(prompt.SupportsNegativePrompt);
        Assert.AreEqual(string.Empty, prompt.NegativePrompt);
        Assert.StartsWith(PlainSelfiePromptComposer.FRAMING + ": older man with a white beard.", prompt.Prompt);
        Assert.Contains("Laughing, looking into the camera.", prompt.Prompt);
        Assert.Contains("Wearing a gray raincoat.", prompt.Prompt);
        Assert.Contains(PlainSelfiePromptComposer.SOAKED, prompt.Prompt);
        Assert.DoesNotContain("(", prompt.Prompt);

        Assert.AreEqual(ImageGenerationStatus.Success, result.Status, result.Problem);
        (Drawing drawing, FaceMatchingFiles? faceMatching) = backend.Drawings.Single();
        Assert.IsNull(faceMatching);
        Assert.IsNull(drawing.Face);
        Assert.IsNull(drawing.NegativePrompt);
        Assert.AreEqual(prompt.Prompt, drawing.Prompt);
        GeneratedImage reference = drawing.References!.Single();
        Assert.AreEqual(3, reference.Channels);
        Assert.AreEqual(0, reference.Width % 16);
        Assert.AreEqual(FaceReference.MIN_REFERENCE_SIDE, reference.Height, "A tiny photo is scaled up.");
        Assert.AreEqual(new ImageGenerationSettings(896, 1152, "euler", null, 4, 1f), backend.Requests.Single().Settings);
    }

    [TestMethod]
    public async Task Generator_AppliesTheLorasThatFit_WithTheirTriggerWordsFirst()
    {
        using TestFolder folder = new();
        FakeBackend backend = new();
        backend.Gpus.Add(CARD);
        StubLoras loras = new();
        loras.Uses[(ImageModelFamily.Flux2Klein4B, ImageModelPurpose.Portraits)] = [new ImageLoraUse("style.safetensors", Path.Combine(folder.Path, "style.safetensors"), 0.7f, "ohwx style")];
        loras.Uses[(ImageModelFamily.Flux2Klein4B, ImageModelPurpose.Selfies)] = [new ImageLoraUse("selfie.safetensors", Path.Combine(folder.Path, "selfie.safetensors"), 1f, "film grain, ")];
        (StableDiffusionImageGenerator generator, _) = CreateGenerator(folder.Path, backend, loras);

        ImageGenerationResult photo = await generator.GenerateAsync(new ImageGenerationRequest("A man in a hat.", 3), cancellationToken: TestContext.CancellationToken);
        SelfiePrompt selfie = generator.ComposePrompt(new SelfieImageRequest([new SelfieSubject("Walt", FakeImageGenerator.TinyPng(1), "older man")], SelfieScene.Unknown, 1));

        Assert.AreEqual(ImageGenerationStatus.Success, photo.Status, photo.Problem);
        Drawing drawing = backend.Drawings.Single().Drawing;
        Assert.AreEqual("ohwx style. A man in a hat.", drawing.Prompt);
        Assert.AreEqual(0.7f, drawing.Loras!.Single().Strength);
        Assert.StartsWith("film grain. " + PlainSelfiePromptComposer.FRAMING, selfie.Prompt);
        Assert.AreEqual("a, b, prompt", LoraTriggers.Prepend("prompt", [new("1", "1", 1, "a"), new("2", "2", 1, "b"), new("3", "3", 1, "prompt")], ImagePromptStyle.Weighted));
    }

    [TestMethod]
    public async Task Lookup_RefusesSdxlAndKleinOnTheProcessor_AndOffersThemOnAGraphicsCard()
    {
        using TestFolder folder = new();
        FakeHub hub = new();
        hub.AddRepository("someone/mixed", REVISION, "apache-2.0", "text-to-image",
            ("sd1.gguf", GgufTestFile.StableDiffusion1()),
            ("sdxl.gguf", GgufTestFile.Write([], GgufTestFile.StableDiffusion(contextWidth: 2048, classEmbedding: true))),
            ("klein.gguf", KleinGguf(GgufImageInspector.KLEIN_4B_TEXT_WIDTH)),
            ("klein-9b.gguf", KleinGguf(12288)));
        ImageModelLibrary library = new([], folder.Path, hub, new ImageGenerationGate(), NullLogger<ImageModelLibrary>.Instance);

        ImageModelLookup cpu = await library.LookUpAsync("someone/mixed", ImageProcessor.Cpu, TestContext.CancellationToken);
        ImageModelLookup gpu = await library.LookUpAsync("someone/mixed", ImageProcessor.Gpu, TestContext.CancellationToken);
        ImageModelFile File(ImageModelLookup lookup, string name)
        {
            return lookup.Files.Single(file => file.File.Path == name);
        }

        Assert.IsTrue(File(cpu, "sd1.gguf").IsSupported);
        Assert.Contains("graphics card", File(cpu, "sdxl.gguf").Problem!);
        Assert.Contains("graphics card", File(cpu, "klein.gguf").Problem!);
        Assert.IsTrue(File(gpu, "sd1.gguf").IsSupported);
        Assert.IsTrue(File(gpu, "sdxl.gguf").IsSupported);
        Assert.AreEqual(ImageModelFamily.Flux2Klein4B, File(gpu, "klein.gguf").Family);
        Assert.IsTrue(File(gpu, "klein.gguf").IsSupported);
        Assert.IsFalse(File(gpu, "klein-9b.gguf").IsSupported);
        Assert.Contains("[klein] 4B", File(gpu, "klein-9b.gguf").Problem!);

        LocalModelAddResult refused = await library.AddAsync(cpu, File(cpu, "klein.gguf"), ImageModelCatalog.KLEIN_SETTINGS, cancellationToken: TestContext.CancellationToken);
        LocalModelAddResult added = await library.AddAsync(gpu, File(gpu, "klein.gguf"), ImageModelCatalog.KLEIN_SELFIE_SETTINGS, ImageModelPurpose.Selfies, cancellationToken: TestContext.CancellationToken);
        LocalModelAddResult weighted = await library.AddAsync(gpu, File(gpu, "sdxl.gguf"), ImageModelCatalog.DREAMSHAPER_XL_SETTINGS, ImageModelPurpose.Portraits, ImagePromptStyle.Plain, TestContext.CancellationToken);

        Assert.IsFalse(refused.IsSuccessful);
        Assert.IsTrue(added.IsSuccessful, added.Problem);
        ImageModelDefinition klein = library.Find(added.ModelId)!;
        Assert.AreEqual(ImageProcessor.Gpu, klein.Processor);
        Assert.AreEqual(ImageModelFamily.Flux2Klein4B, klein.Family);
        Assert.AreEqual(ImagePromptStyle.Plain, klein.Style);
        Assert.AreEqual(SelfieFaceSource.ReferenceImage, klein.FaceSource);
        Assert.AreSequenceEqual(ImageModelCatalog.KleinComponents.ToArray(), klein.SharedFiles.ToArray());
        Assert.AreEqual(ImagePromptStyle.Plain, library.Find(weighted.ModelId)!.Style, "The prompt style the user chose is kept.");

        // Kept across restarts, with what makes them graphics card models.
        ImageModelLibrary reopened = new([], folder.Path, hub, new ImageGenerationGate(), NullLogger<ImageModelLibrary>.Instance);
        ImageModelDefinition kept = reopened.Find(added.ModelId)!;
        Assert.AreEqual(ImageProcessor.Gpu, kept.Processor);
        Assert.AreEqual(ImageModelFamily.Flux2Klein4B, kept.Family);
        Assert.AreSequenceEqual(klein.SharedFiles.ToArray(), kept.SharedFiles.ToArray());
        Assert.AreEqual(ImagePromptStyle.Plain, reopened.Find(weighted.ModelId)!.Style);
    }

    [TestMethod]
    public async Task AddDialog_StartsTheSettingsAndPromptStyleFromTheFilesFamily()
    {
        using TestFolder folder = new();
        FakeHub hub = new();
        hub.AddRepository("someone/mixed", REVISION, "apache-2.0", "text-to-image",
            ("sdxl.gguf", GgufTestFile.Write([], GgufTestFile.StableDiffusion(contextWidth: 2048, classEmbedding: true))),
            ("klein.gguf", KleinGguf(GgufImageInspector.KLEIN_4B_TEXT_WIDTH)));
        ImageModelLibrary library = new([], folder.Path, hub, new ImageGenerationGate(), NullLogger<ImageModelLibrary>.Instance);
        AddImageModelViewModel dialog = new(library, ImageModelPurpose.Selfies, ImageProcessor.Gpu) { Repository = "someone/mixed" };

        await dialog.LookUpCommand.ExecuteAsync(null);
        dialog.SelectedFile = dialog.Files.Single(file => file.Name == "klein.gguf");

        Assert.AreEqual(ImagePromptStyle.Plain, dialog.PromptStyle);
        Assert.AreEqual(new ImageGenerationSettings(896, 1152, "euler", null, 4, 1f), dialog.Settings);
        Assert.Contains("not used", dialog.NegativePromptHint);
        Assert.Contains("from their photo itself", dialog.SettingsHint);

        dialog.SelectedFile = dialog.Files.Single(file => file.Name == "sdxl.gguf");

        Assert.AreEqual(ImagePromptStyle.Weighted, dialog.PromptStyle);
        Assert.AreEqual(new ImageGenerationSettings(896, 1152, AddImageModelViewModel.DEFAULT_SAMPLER, null, AddImageModelViewModel.DEFAULT_SDXL_STEPS, 7f), dialog.Settings);
        Assert.Contains("are used", dialog.NegativePromptHint);
    }

    [TestMethod]
    public void Inspect_Klein4B_IsRecognizedByItsTensors_AndLargerFlux2Refused()
    {
        GgufInspection klein = GgufImageInspector.Inspect(KleinGguf(GgufImageInspector.KLEIN_4B_TEXT_WIDTH));
        GgufInspection larger = GgufImageInspector.Inspect(KleinGguf(12288));
        GgufInspection declared = GgufImageInspector.Inspect(GgufTestFile.Write([("general.architecture", "flux")], KleinTensors(GgufImageInspector.KLEIN_4B_TEXT_WIDTH)));

        Assert.AreEqual(ImageModelFamily.Flux2Klein4B, klein.Family);
        Assert.IsNull(klein.Problem);
        Assert.AreEqual("Q4_0", klein.Quantization);
        Assert.AreEqual(ImageModelFamily.OtherImageModel, larger.Family);
        Assert.IsNotNull(larger.Problem);
        Assert.AreEqual(ImageModelFamily.Flux2Klein4B, declared.Family, "A declared image architecture is read on into its tensors.");
    }

    [TestMethod]
    public void NativeLayouts_MatchThePinnedHeader()
    {
        Assert.AreEqual(16, StableDiffusionNative.LoraLayout.SIZE);
        Assert.AreEqual(0, StableDiffusionNative.LoraLayout.IS_HIGH_NOISE);
        Assert.AreEqual(4, StableDiffusionNative.LoraLayout.MULTIPLIER);
        Assert.AreEqual(8, StableDiffusionNative.LoraLayout.PATH);
        Assert.AreEqual(24, StableDiffusionNative.ImageLayout.SIZE);
        Assert.AreEqual(16, StableDiffusionNative.ImageLayout.DATA);
        Assert.AreEqual(0, StableDiffusionNative.ImageOffsets.LORAS);
        Assert.AreEqual(8, StableDiffusionNative.ImageOffsets.LORA_COUNT);
        Assert.AreEqual(5 * 8, StableDiffusionNative.ContextOffsets.LLM);
        Assert.AreEqual(7 * 8, StableDiffusionNative.ContextOffsets.DIFFUSION_MODEL);
        Assert.AreEqual(11 * 8, StableDiffusionNative.ContextOffsets.VAE);
        Assert.IsGreaterThan(StableDiffusionNative.ContextOffsets.BACKEND, StableDiffusionNative.ContextOffsets.FLASH_ATTN_MODE);
        Assert.IsLessThan(StableDiffusionNative.PARAMS_BUFFER_BYTES, StableDiffusionNative.ContextOffsets.CONV_DIRECT_MODE);
    }

    [TestMethod]
    public void PlainComposer_WritesTheSceneAsSentences_AndOnlySoaksAWetScene()
    {
        SelfiePrompt dry = PlainSelfiePromptComposer.Compose(new SelfieSubject("A", [], "woman in her thirties", "embarrassed"),
            new SelfieScene("a crowded stadium", null, "a sign that says MISS YOU", null, "night, floodlights"));

        Assert.AreEqual(
            $"{PlainSelfiePromptComposer.FRAMING}: woman in her thirties. Embarrassed, looking into the camera. In the picture with them: a sign that says MISS YOU. A crowded stadium. Night, floodlights.",
            dry.Prompt);
        Assert.DoesNotContain("wet", dry.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(ImagePromptStyle.Plain, dry.Style);
    }

    private static List<(string Name, long[] Shape, uint Type)> KleinTensors(long textWidth)
    {
        long hidden = textWidth == GgufImageInspector.KLEIN_4B_TEXT_WIDTH ? 3072 : 4096;

        return
        [
            ("double_blocks.0.img_attn.qkv.weight", [hidden, hidden * 3], 2),
            ("double_stream_modulation_img.lin.weight", [hidden, hidden * 6], GgufTestFile.F16),
            ("txt_in.weight", [textWidth, hidden], GgufTestFile.F16),
            ("single_blocks.0.linear1.weight", [hidden, hidden * 9], 2)
        ];
    }

    private static byte[] KleinGguf(long textWidth)
    {
        return GgufTestFile.Write([], KleinTensors(textWidth));
    }

    private static FakeHub Hub()
    {
        FakeHub hub = new();
        hub.Files[$"{REPO}/resolve/{REVISION}/klein.gguf"] = KLEIN;
        hub.Files[$"{REPO}/resolve/{REVISION}/tuned.gguf"] = TUNED;
        hub.Files[$"{REPO}/resolve/{REVISION}/text/encoder.gguf"] = TEXT_ENCODER;
        hub.Files[$"{REPO}/resolve/{REVISION}/vae/vae.safetensors"] = VAE;
        return hub;
    }

    private static IReadOnlyList<ImageModelComponent> Components()
    {
        string baseUrl = $"{FakeHub.SITE}{REPO}/resolve/{REVISION}/";
        return
        [
            new("test-text-encoder", "Test text encoder", ImageComponentRole.TextEncoder, "MIT", baseUrl, new LocalModelFile("text/encoder.gguf", TEXT_ENCODER.Length, FakeHub.Sha256(TEXT_ENCODER))),
            new("test-vae", "Test VAE", ImageComponentRole.Vae, "MIT", baseUrl, new LocalModelFile("vae/vae.safetensors", VAE.Length, FakeHub.Sha256(VAE)))
        ];
    }

    private static ImageModelDefinition Klein(string id, byte[] content)
    {
        return new ImageModelDefinition(id, id, "A test model.", "MIT", null, $"{FakeHub.SITE}{REPO}/resolve/{REVISION}/",
            [new LocalModelFile($"{id}.gguf", content.Length, FakeHub.Sha256(content))],
            ImageModelCatalog.KLEIN_SETTINGS, true,
            Purposes: ImageModelPurpose.Portraits | ImageModelPurpose.Selfies,
            SelfieSettings: ImageModelCatalog.KLEIN_SELFIE_SETTINGS,
            Processor: ImageProcessor.Gpu,
            Family: ImageModelFamily.Flux2Klein4B,
            Components: Components());
    }

    private static ImageModelLibrary CreateLibrary(string folder, FakeHub hub)
    {
        return new ImageModelLibrary([Klein("klein", KLEIN), Klein("tuned", TUNED)], folder, hub, new ImageGenerationGate(), NullLogger<ImageModelLibrary>.Instance);
    }

    /// <summary>
    /// A generator over a library whose FLUX.2 [klein]-style model is already on disk, with its shared files.
    /// </summary>
    private static (StableDiffusionImageGenerator Generator, ImageModelLibrary Library) CreateGenerator(string folder, FakeBackend backend, IImageLoraLibrary? loras = null)
    {
        ImageModelDefinition model = Klein("klein", KLEIN);
        ImageGenerationGate gate = new();
        ImageModelLibrary library = new([model], folder, new NoNetwork(), gate, NullLogger<ImageModelLibrary>.Instance);
        ImageModelPaths paths = library.PathsFor(model);

        foreach ((string path, byte[] content) in new[] { (paths.DiffusionModel!, KLEIN), (paths.TextEncoder!, TEXT_ENCODER), (paths.Vae!, VAE) })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        return (new StableDiffusionImageGenerator(library, backend, gate, NullLogger<StableDiffusionImageGenerator>.Instance, loras: loras), library);
    }

    private sealed class StubLoras : IImageLoraLibrary
    {
        public Dictionary<(ImageModelFamily, ImageModelPurpose), IReadOnlyList<ImageLoraUse>> Uses { get; } = [];

        public string? Folder => null;

        public string? SetFolder(string? folder)
        {
            return null;
        }

        public Task<IReadOnlyList<ImageLora>> ScanAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ImageLora>>([]);
        }

        public string? Save(string name, ImageLoraSettings settings)
        {
            return null;
        }

        public IReadOnlyList<ImageLoraUse> InUse(ImageModelFamily family, ImageModelPurpose purpose)
        {
            return Uses.GetValueOrDefault((family, purpose)) ?? [];
        }
    }
}
