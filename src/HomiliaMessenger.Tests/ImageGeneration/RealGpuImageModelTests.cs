using HomiliaMessenger.Build;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Integrations.ImageGeneration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.ImageGeneration;

/// <summary>
/// FLUX.2 [klein] 4B on a real graphics card, through the app's own binding of the runtime. Set
/// <c>HOMILIA_GPU_TEST_MODELS</c> to a folder holding <c>flux-2-klein-4b-Q4_0.gguf</c>, <c>Qwen3-4B-Q4_K_M.gguf</c>, and
/// <c>flux2-vae.safetensors</c> (as the GPU image experiment's <c>models</c> folder does); these tests are inconclusive
/// otherwise, and without a graphics card. They run on the included runtime's Vulkan backend, or on the CUDA runtime
/// unpacked in <c>HOMILIA_GPU_TEST_CUDA_DIR</c> when set. Only one runtime can load in a process, so run this category apart
/// from <c>ImageModel</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RealGpuImageModelTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("GpuImageModel")]
    public async Task RealKlein_DrawsAContactPhotoOnTheGraphicsCard()
    {
        (StableDiffusionImageGenerator generator, NativeStableDiffusionBackend backend) = CreateGenerator();
        List<ImageGenerationProgress> updates = [];
        Stopwatch watch = Stopwatch.StartNew();

        ImageGenerationResult result = await generator.GenerateAsync(
            new ImageGenerationRequest("Head-and-shoulders portrait of a middle-aged woman with curly red hair and freckles, wearing a green knit sweater, in a sunny kitchen. Photorealistic.", 42),
            new SynchronousProgress(updates), TestContext.CancellationToken);

        watch.Stop();
        Assert.AreEqual(ImageGenerationStatus.Success, result.Status, result.Problem);
        (int width, int height, byte[] pixels) = PngTestDecoder.Decode(result.Png!);
        TestContext.WriteLine($"{backend.GpuDevices()[0].Name} ({(backend.IsCudaActive ? "CUDA runtime" : "included runtime")}): {watch.Elapsed.TotalSeconds:0.0} s.");
        Save(result.Png!, "klein-photo.png");

        Assert.AreEqual(1024, width);
        Assert.AreEqual(1024, height);
        Assert.IsGreaterThan(20.0, StandardDeviation(pixels), "The image has detail, not one flat color.");
        Assert.Contains(update => update.Stage == ImageGenerationStage.Sampling && update.Total == 4, updates);
    }

    [TestMethod]
    [TestCategory("GpuImageModel")]
    public async Task RealKlein_DrawsASelfieFromTheContactsPhotoAsAReference()
    {
        string photoPath = Environment.GetEnvironmentVariable("HOMILIA_SELFIE_TEST_PHOTO") ?? string.Empty;

        if (!File.Exists(photoPath))
        {
            Assert.Inconclusive("Set HOMILIA_SELFIE_TEST_PHOTO to a portrait to keep the face of.");
        }

        (StableDiffusionImageGenerator generator, NativeStableDiffusionBackend backend) = CreateGenerator();
        SelfieSubject subject = new("Walt", File.ReadAllBytes(photoPath), "middle-aged man with short gray hair and a gray goatee", "laughing");
        SelfieImageRequest request = new([subject], new SelfieScene("a New Year's Eve party, confetti and string lights", "a champagne bottle pops open and sprays champagne all over him"), 101);
        Stopwatch watch = Stopwatch.StartNew();

        ImageGenerationResult result = await generator.GenerateAsync(request, cancellationToken: TestContext.CancellationToken);

        watch.Stop();
        Assert.AreEqual(ImageGenerationStatus.Success, result.Status, result.Problem);
        (int width, int height, byte[] pixels) = PngTestDecoder.Decode(result.Png!);
        TestContext.WriteLine($"{backend.GpuDevices()[0].Name}: {watch.Elapsed.TotalSeconds:0.0} s. Prompt: {generator.ComposePrompt(request).Prompt}");
        Save(result.Png!, "klein-selfie.png");

        Assert.AreEqual(896, width);
        Assert.AreEqual(1152, height);
        Assert.IsGreaterThan(20.0, StandardDeviation(pixels));
    }

    [TestMethod]
    [TestCategory("GpuImageModel")]
    public void RealRuntime_ListsTheGraphicsCards()
    {
        (_, NativeStableDiffusionBackend backend) = CreateGenerator();

        IReadOnlyList<ImageGpuDevice> devices = backend.GpuDevices();

        foreach (ImageGpuDevice device in devices)
        {
            TestContext.WriteLine($"{device.Name}: {device.Description}, {device.TotalBytes / 1e9:0.0} GB{(device.IsIntegrated ? ", integrated" : string.Empty)}");
        }

        Assert.IsNotEmpty(devices);
        Assert.AreEqual(backend.IsCudaActive, devices[0].IsCuda, "The CUDA runtime's card comes first when it is in use.");
    }

    /// <summary>
    /// Unpacks the CUDA runtime from its two release archives, as downloading it in Settings does, when
    /// <c>HOMILIA_CUDA_TEST_ARCHIVES</c> names a folder holding them (so they are not downloaded again). Loads nothing.
    /// </summary>
    [TestMethod]
    [TestCategory("GpuImageModel")]
    public async Task RealCudaArchives_UnpackIntoARuntimeWithItsGgmlRenamed()
    {
        string archives = Environment.GetEnvironmentVariable("HOMILIA_CUDA_TEST_ARCHIVES") ?? string.Empty;

        if (!File.Exists(Path.Combine(archives, CudaRuntime.RUNTIME_ARCHIVE.Name)) || !File.Exists(Path.Combine(archives, CudaRuntime.CUDA_ARCHIVE.Name)))
        {
            Assert.Inconclusive("Set HOMILIA_CUDA_TEST_ARCHIVES to a folder holding the pinned CUDA release archives.");
        }

        using TestFolder folder = new();
        CudaRuntime cuda = new(folder.Path, new FakeBackend(), new FakeHub(), NullLogger<CudaRuntime>.Instance);

        // Placed where an interrupted download would have left them, so the download resumes with nothing to fetch.
        string partial = cuda.RuntimeDirectory + "-archives.download";
        Directory.CreateDirectory(partial);

        foreach (string archive in new[] { CudaRuntime.RUNTIME_ARCHIVE.Name, CudaRuntime.CUDA_ARCHIVE.Name })
        {
            File.Copy(Path.Combine(archives, archive), Path.Combine(partial, archive));
        }

        Business.Modules.Agents.LocalModelDownloadResult result = await cuda.DownloadCudaAsync(cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(Business.Modules.Agents.LocalModelDownloadStatus.Success, result.Status, result.Problem);
        Assert.IsTrue(CudaRuntime.IsComplete(cuda.RuntimeDirectory));
        Assert.IsFalse(Directory.Exists(cuda.RuntimeDirectory + "-archives"), "The archives are removed once unpacked.");
        string[] shared = ["ggml.dll", "ggml-base.dll"];

        foreach (string library in Directory.GetFiles(cuda.RuntimeDirectory, "*.dll").Where(path => !Path.GetFileName(path).StartsWith("cu", StringComparison.OrdinalIgnoreCase)))
        {
            IReadOnlyList<string> names = NativeLibraryRenamer.Names(File.ReadAllBytes(library));
            Assert.DoesNotContain(name => shared.Contains(name, StringComparer.OrdinalIgnoreCase), names, $"{Path.GetFileName(library)} still names the shared ggml.");
        }

        string target = Environment.GetEnvironmentVariable("HOMILIA_GPU_TEST_CUDA_DIR") ?? string.Empty;

        if (target.Length > 0 && !Directory.Exists(target))
        {
            // Kept for a later run of this category on the CUDA runtime.
            Directory.Move(cuda.RuntimeDirectory, target);
            TestContext.WriteLine($"Unpacked the CUDA runtime into {target}.");
        }
    }

    /// <summary>
    /// A LoRA turned on in the LoRA library changes what DreamShaper XL Lightning draws from the same seed, through the app's
    /// own path from its settings to the runtime. Set <c>HOMILIA_GPU_TEST_SDXL_DIR</c> to a folder holding
    /// <c>DreamShaperXL_Lightning.safetensors</c> and <c>HOMILIA_GPU_TEST_LORA</c> to an SDXL LoRA; one without trigger
    /// words shows the change comes from its weights alone.
    /// </summary>
    [TestMethod]
    [TestCategory("GpuImageModel")]
    public async Task RealDreamShaperXl_ALoraTurnedOnChangesTheImage()
    {
        string models = Environment.GetEnvironmentVariable("HOMILIA_GPU_TEST_SDXL_DIR") ?? string.Empty;
        string loraPath = Environment.GetEnvironmentVariable("HOMILIA_GPU_TEST_LORA") ?? string.Empty;
        ImageModelDefinition xl = ImageModelCatalog.Models.Single(model => model.Id == ImageModelCatalog.DREAMSHAPER_XL_LIGHTNING);

        if (!File.Exists(Path.Combine(models, xl.Files[0].LocalName)) || !File.Exists(loraPath))
        {
            Assert.Inconclusive("Set HOMILIA_GPU_TEST_SDXL_DIR to a folder holding DreamShaper XL Lightning and HOMILIA_GPU_TEST_LORA to an SDXL LoRA.");
        }

        NativeStableDiffusionBackend backend = CreateBackend();
        using TestFolder folder = new();
        ImageGenerationGate gate = new();
        ImageModelDefinition inPlace = xl with { Id = models };
        ImageModelLibrary library = new([inPlace], folder.Path, new NoNetwork(), gate, NullLogger<ImageModelLibrary>.Instance, backend: backend);
        ImageLoraLibrary loras = new(folder.Path, NullLogger<ImageLoraLibrary>.Instance);
        StableDiffusionImageGenerator generator = new(library, backend, gate, NullLogger<StableDiffusionImageGenerator>.Instance, loras: loras);
        ImageGenerationRequest request = new("Head-and-shoulders portrait of a young woman with straight black hair, in a sunny kitchen. Photorealistic.", 7);

        Assert.IsNull(loras.SetFolder(Path.GetDirectoryName(loraPath)));
        string name = Path.GetFileName(loraPath);
        ImageLora found = (await loras.ScanAsync(TestContext.CancellationToken)).Single(lora => lora.Name == name);
        Assert.AreEqual(ImageModelFamily.StableDiffusionXL, found.Base, "The LoRA is read as SDXL.");

        ImageGenerationResult without = await generator.GenerateAsync(request, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(ImageGenerationStatus.Success, without.Status, without.Problem);

        Assert.IsNull(loras.Save(name, found.Settings with { IsEnabled = true, Strength = 1f }));
        Assert.HasCount(1, loras.InUse(ImageModelFamily.StableDiffusionXL, ImageModelPurpose.Portraits));
        Stopwatch watch = Stopwatch.StartNew();
        ImageGenerationResult with = await generator.GenerateAsync(request, cancellationToken: TestContext.CancellationToken);
        watch.Stop();
        Assert.AreEqual(ImageGenerationStatus.Success, with.Status, with.Problem);

        Assert.IsNull(loras.Save(name, found.Settings with { IsEnabled = false }));
        ImageGenerationResult offAgain = await generator.GenerateAsync(request, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(ImageGenerationStatus.Success, offAgain.Status, offAgain.Problem);

        byte[] plain = PngTestDecoder.Decode(without.Png!).Pixels;
        double changed = MeanDifference(plain, PngTestDecoder.Decode(with.Png!).Pixels);
        double restored = MeanDifference(plain, PngTestDecoder.Decode(offAgain.Png!).Pixels);
        TestContext.WriteLine($"{backend.GpuDevices()[0].Name}: with the LoRA in {watch.Elapsed.TotalSeconds:0.0} s; mean difference with it {changed:0.00}, after turning it off {restored:0.00} (of 255).");

        Assert.IsGreaterThan(3.0, changed, "The LoRA changes the image.");
        Assert.IsLessThan(0.5, restored, "Turning the LoRA off draws the image as before.");
    }

    private static double MeanDifference(byte[] first, byte[] second)
    {
        Assert.HasCount(first.Length, second);
        return first.Zip(second, (a, b) => (double)Math.Abs(a - b)).Average();
    }

    private NativeStableDiffusionBackend CreateBackend()
    {
        string? cuda = Environment.GetEnvironmentVariable("HOMILIA_GPU_TEST_CUDA_DIR");
        NativeStableDiffusionBackend backend = new(AppContext.BaseDirectory, NullLogger<NativeStableDiffusionBackend>.Instance, string.IsNullOrEmpty(cuda) ? null : cuda);

        if (backend.RuntimeProblem is { } problem)
        {
            Assert.Fail(problem);
        }

        if (backend.GpuDevices().Count == 0)
        {
            Assert.Inconclusive("This computer has no graphics card the runtime can use.");
        }

        return backend;
    }

    private (StableDiffusionImageGenerator Generator, NativeStableDiffusionBackend Backend) CreateGenerator()
    {
        string models = Environment.GetEnvironmentVariable("HOMILIA_GPU_TEST_MODELS") ?? string.Empty;
        string[] files = ["flux-2-klein-4b-Q4_0.gguf", "Qwen3-4B-Q4_K_M.gguf", "flux2-vae.safetensors"];

        if (!files.All(file => File.Exists(Path.Combine(models, file))))
        {
            Assert.Inconclusive("Set HOMILIA_GPU_TEST_MODELS to a folder holding FLUX.2 [klein] 4B Q4_0, Qwen3-4B Q4_K_M, and the FLUX.2 VAE.");
        }

        string? cuda = Environment.GetEnvironmentVariable("HOMILIA_GPU_TEST_CUDA_DIR");
        NativeStableDiffusionBackend backend = new(AppContext.BaseDirectory, NullLogger<NativeStableDiffusionBackend>.Instance, string.IsNullOrEmpty(cuda) ? null : cuda);

        if (backend.RuntimeProblem is { } problem)
        {
            Assert.Fail(problem);
        }

        if (backend.GpuDevices().Count == 0)
        {
            Assert.Inconclusive("This computer has no graphics card the runtime can use.");
        }

        // The files are used where they are: each shared file's ID names its folder, and the model's ID its own.
        ImageModelDefinition klein = ImageModelCatalog.Models.Single(model => model.Id == ImageModelCatalog.FLUX2_KLEIN_4B);
        ImageModelComponent[] components = [.. klein.SharedFiles.Select(component => component with { Id = models, File = component.File with { Name = files[component.Role == ImageComponentRole.TextEncoder ? 1 : 2] } })];
        ImageModelDefinition inPlace = klein with { Id = models, Components = components };
        ImageGenerationGate gate = new();
        ImageModelLibrary library = new([inPlace], Path.Combine(Path.GetTempPath(), "HomiliaMessenger-gpu-tests"), new NoNetwork(), gate, NullLogger<ImageModelLibrary>.Instance, backend: backend);
        ImageModelPaths paths = library.PathsFor(inPlace);

        Assert.IsTrue(library.IsDownloaded(inPlace), $"Expected {paths.DiffusionModel}, {paths.TextEncoder}, and {paths.Vae}.");

        return (new StableDiffusionImageGenerator(library, backend, gate, NullLogger<StableDiffusionImageGenerator>.Instance), backend);
    }

    /// <summary>
    /// Keeps an image beside the test results, to look at.
    /// </summary>
    private void Save(byte[] png, string name)
    {
        string path = Path.Combine(TestContext.TestRunResultsDirectory ?? Path.GetTempPath(), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, png);
        TestContext.WriteLine($"Saved {path}.");
    }

    private static double StandardDeviation(byte[] pixels)
    {
        double mean = pixels.Average(value => (double)value);
        return Math.Sqrt(pixels.Average(value => (value - mean) * (value - mean)));
    }
}
