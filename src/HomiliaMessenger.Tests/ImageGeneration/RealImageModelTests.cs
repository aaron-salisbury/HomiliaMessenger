using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Integrations.ImageGeneration;
using HomiliaMessenger.Integrations.LocalModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.ImageGeneration;

/// <summary>
/// Generates with the real DreamShaper 8 LCM checkpoint and the stable-diffusion.cpp runtime built with the app. Set
/// <c>HOMILIA_IMAGE_TEST_MODEL</c> to the checkpoint's path, or download it in Settings; these tests are inconclusive
/// otherwise, so the suite never needs the 1.8 GB download.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RealImageModelTests
{
    /// <summary>
    /// The local portrait experiment's in-process park image (seed 42, 512×512, LCM, 4 steps, CFG 1): the SHA-256 of its
    /// decoded RGB pixels, which matched the native CLI's image pixel for pixel.
    /// </summary>
    private const string EXPERIMENT_PARK_PIXELS = "E78D5CEC0738A68D61849F2833A63B01480D944A26A42374592DFB1D3A471E8C";

    private const string EXPERIMENT_PARK_PROMPT = "Candid, believable personal profile photograph, natural daylight or available light, ordinary clothing, realistic skin texture, relaxed expression, simple composition, no glamour retouching, no dramatic lighting. A casually dressed young adult with medium brown skin and curly hair outdoors in a neighborhood park, slightly off-center, trees in the background.";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("ImageModel")]
    public async Task RealModel_DrawsTheExperimentsImagePixelForPixel_WithLlamaSharpLoaded()
    {
        StableDiffusionImageGenerator generator = CreateGenerator(out string modelPath);

        // Conversation models' ggml is loaded first, as it is once a contact has replied; the image runtime's renamed
        // copy must load beside it.
        LLama.Native.NativeApi.llama_max_devices();

        long peak = 0;
        using Timer sampler = new(_ =>
        {
            long current = Process.GetCurrentProcess().WorkingSet64;
            Interlocked.Exchange(ref peak, Math.Max(Interlocked.Read(ref peak), current));
        }, null, 0, 100);
        List<ImageGenerationProgress> updates = [];
        Stopwatch watch = Stopwatch.StartNew();

        ImageGenerationResult result = await generator.GenerateAsync(
            new ImageGenerationRequest(EXPERIMENT_PARK_PROMPT, 42), new SynchronousProgress(updates), TestContext.CancellationToken);

        watch.Stop();
        Assert.AreEqual(ImageGenerationStatus.Success, result.Status, result.Problem);
        (int width, int height, byte[] pixels) = PngTestDecoder.Decode(result.Png!);
        TestContext.WriteLine($"{Path.GetFileName(modelPath)}: {watch.Elapsed.TotalSeconds:0.0} s, peak working set {Interlocked.Read(ref peak) / 1e9:0.00} GB, PNG {result.Png!.Length / 1024} KiB.");

        Assert.AreEqual(512, width);
        Assert.AreEqual(512, height);
        Assert.AreEqual(EXPERIMENT_PARK_PIXELS, Convert.ToHexString(SHA256.HashData(pixels)));
        Assert.Contains(update => update.Stage == ImageGenerationStage.Sampling && update.Total == 4, updates);
        Assert.Contains(update => update.Stage == ImageGenerationStage.Decoding, updates);
    }

    [TestMethod]
    [TestCategory("ImageModel")]
    public async Task RealModel_StopsWhenCanceledWhileDrawing_AndCanDrawAgain()
    {
        StableDiffusionImageGenerator generator = CreateGenerator(out _);
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        Stopwatch sinceCancel = new();
        SynchronousProgress progress = new([], update =>
        {
            if (update.Stage == ImageGenerationStage.Sampling && update.Completed == 1 && !cancel.IsCancellationRequested)
            {
                sinceCancel.Start();
                cancel.Cancel();
            }
        });

        ImageGenerationResult canceled = await generator.GenerateAsync(new ImageGenerationRequest("Older man with a white beard.", 7), progress, cancel.Token);
        sinceCancel.Stop();
        TestContext.WriteLine($"Stopped {sinceCancel.Elapsed.TotalSeconds:0.0} s after canceling during sampling.");
        ImageGenerationResult again = await generator.GenerateAsync(new ImageGenerationRequest("Older man with a white beard.", 7), null, TestContext.CancellationToken);

        Assert.AreEqual(ImageGenerationStatus.Canceled, canceled.Status);
        Assert.IsLessThan(10, sinceCancel.Elapsed.TotalSeconds);
        Assert.AreEqual(ImageGenerationStatus.Success, again.Status, again.Problem);
    }

    [TestMethod]
    [TestCategory("ImageModel")]
    public void RealModel_HeaderShowsACompleteStableDiffusion1Model()
    {
        string modelPath = ModelPath();
        byte[] header = new byte[ImageModelLibrary.FIRST_HEADER_READ];

        using (FileStream file = File.OpenRead(modelPath))
        {
            file.ReadExactly(header);
        }

        GgufInspection inspection = GgufImageInspector.Inspect(header);

        Assert.AreEqual(ImageModelFamily.StableDiffusion1, inspection.Family);
        Assert.IsTrue(inspection.HasTextEncoder);
        Assert.IsTrue(inspection.HasVae);
        Assert.AreEqual("Q8_0", inspection.Quantization);
        Assert.IsTrue(inspection.IsSupported);
    }

    private static string ModelPath()
    {
        string modelPath = Environment.GetEnvironmentVariable("HOMILIA_IMAGE_TEST_MODEL")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomiliaMessenger", "Models", "Images",
                ImageModelCatalog.DREAMSHAPER_8_LCM, ImageModelCatalog.Models[0].Checkpoint.LocalName);

        if (!File.Exists(modelPath))
        {
            Assert.Inconclusive("DreamShaper 8 LCM is not downloaded on this machine; set HOMILIA_IMAGE_TEST_MODEL to its path.");
        }

        return modelPath;
    }

    private StableDiffusionImageGenerator CreateGenerator(out string modelPath)
    {
        modelPath = ModelPath();

        // A catalog of the checkpoint where it is, so the 1.8 GB file is used in place rather than copied.
        string folder = Path.GetDirectoryName(modelPath)!;
        ImageModelDefinition dreamShaper = ImageModelCatalog.Models[0];
        ImageModelDefinition inPlace = dreamShaper with
        {
            Id = Path.GetFileName(folder),
            Files = [new LocalModelFile(Path.GetFileName(modelPath), new FileInfo(modelPath).Length, dreamShaper.Checkpoint.Sha256)]
        };
        ImageGenerationGate gate = new();
        ImageModelLibrary library = new([inPlace], Path.GetDirectoryName(folder)!, new NoNetwork(), gate, NullLogger<ImageModelLibrary>.Instance);
        NativeStableDiffusionBackend backend = new(AppContext.BaseDirectory, NullLogger<NativeStableDiffusionBackend>.Instance);

        if (backend.RuntimeProblem is { } problem)
        {
            Assert.Fail(problem);
        }

        return new StableDiffusionImageGenerator(library, backend, gate, NullLogger<StableDiffusionImageGenerator>.Instance);
    }
}
