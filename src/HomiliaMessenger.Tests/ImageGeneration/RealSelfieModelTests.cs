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
/// Selfies with the real DreamShaper 8 LCM checkpoint, face matching, and the stable-diffusion.cpp runtime built with the
/// app. Set <c>HOMILIA_IMAGE_TEST_MODEL</c> to the checkpoint, <c>HOMILIA_FACE_MATCHING_TEST_DIR</c> to a folder named
/// <c>face-matching</c> holding the adapter and the prepared encoder (or download them in Settings), and
/// <c>HOMILIA_IMAGE_ENCODER_TEST_FILE</c> to the full-precision encoder as published; these tests are inconclusive
/// otherwise.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RealSelfieModelTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("ImageModel")]
    public void RealRuntime_PreparesTheImageEncoderAsTheExperimentsCommandLineDid()
    {
        string source = Environment.GetEnvironmentVariable("HOMILIA_IMAGE_ENCODER_TEST_FILE") ?? string.Empty;

        if (!File.Exists(source) || new FileInfo(source).Length != FaceMatchingCatalog.IMAGE_ENCODER.Size)
        {
            Assert.Inconclusive("Set HOMILIA_IMAGE_ENCODER_TEST_FILE to h94/IP-Adapter's models/image_encoder/model.safetensors.");
        }

        NativeStableDiffusionBackend backend = new(AppContext.BaseDirectory, NullLogger<NativeStableDiffusionBackend>.Instance);
        string output = Path.Combine(Path.GetTempPath(), $"homilia-encoder-{Guid.NewGuid():N}.gguf");

        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            backend.ConvertToQ8(source, output);
            TestContext.WriteLine($"Converted in {watch.Elapsed.TotalSeconds:0.0} s.");

            Assert.AreEqual(FaceMatchingCatalog.PREPARED_ENCODER_BYTES, new FileInfo(output).Length);
            using FileStream file = File.OpenRead(output);
            Assert.AreEqual(FaceMatchingCatalog.PREPARED_ENCODER_SHA256, Convert.ToHexString(SHA256.HashData(file)), ignoreCase: true);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [TestMethod]
    [TestCategory("ImageModel")]
    public async Task RealModel_DrawsASelfieThatKeepsTheFace_WithLlamaSharpLoaded()
    {
        StableDiffusionImageGenerator generator = CreateGenerator();
        LLama.Native.NativeApi.llama_max_devices();

        // The experiment's workshop portrait: an older man with a full white beard and eyeglasses.
        byte[] photo = ReferencePhoto();
        SelfieSubject subject = new("Walt", photo, "older white man with a full white beard and eyeglasses", "laughing");
        SelfieScene scene = new("a bus stop on a city street", null, null, "pouring rain", "gray daylight");

        long peak = 0;
        using Timer sampler = new(_ =>
        {
            long current = Process.GetCurrentProcess().WorkingSet64;
            Interlocked.Exchange(ref peak, Math.Max(Interlocked.Read(ref peak), current));
        }, null, 0, 100);
        List<ImageGenerationProgress> updates = [];
        Stopwatch watch = Stopwatch.StartNew();

        ImageGenerationResult result = await generator.GenerateAsync(new SelfieImageRequest([subject], scene, 101), new SynchronousProgress(updates), TestContext.CancellationToken);

        watch.Stop();
        Assert.AreEqual(ImageGenerationStatus.Success, result.Status, result.Problem);
        (int width, int height, _) = PngTestDecoder.Decode(result.Png!);
        string output = Path.Combine(TestContext.TestRunResultsDirectory ?? Path.GetTempPath(), "real-selfie-101.png");
        File.WriteAllBytes(output, result.Png!);
        TestContext.WriteLine($"{watch.Elapsed.TotalSeconds:0.0} s, peak working set {Interlocked.Read(ref peak) / 1e9:0.00} GB, saved to {output}.");

        Assert.AreEqual(512, width);
        Assert.AreEqual(640, height);
        Assert.Contains(update => update.Stage == ImageGenerationStage.Sampling && update.Total == 4, updates);
    }

    private static byte[] ReferencePhoto()
    {
        string path = Environment.GetEnvironmentVariable("HOMILIA_SELFIE_TEST_PHOTO") ?? string.Empty;

        if (!File.Exists(path))
        {
            Assert.Inconclusive("Set HOMILIA_SELFIE_TEST_PHOTO to a portrait to keep the face of.");
        }

        return File.ReadAllBytes(path);
    }

    private static StableDiffusionImageGenerator CreateGenerator()
    {
        string modelPath = Environment.GetEnvironmentVariable("HOMILIA_IMAGE_TEST_MODEL") ?? string.Empty;
        string faceMatching = Environment.GetEnvironmentVariable("HOMILIA_FACE_MATCHING_TEST_DIR") ?? string.Empty;

        if (!File.Exists(modelPath) || !Directory.Exists(faceMatching) || Path.GetFileName(faceMatching.TrimEnd('\\', '/')) != FaceMatchingCatalog.DIRECTORY_NAME)
        {
            Assert.Inconclusive("Set HOMILIA_IMAGE_TEST_MODEL to DreamShaper 8 LCM and HOMILIA_FACE_MATCHING_TEST_DIR to a face-matching folder.");
        }

        // The checkpoint is used where it is: an ID that is a full path makes the library's folder for it that path.
        ImageModelDefinition dreamShaper = ImageModelCatalog.Models[0];
        ImageModelDefinition inPlace = dreamShaper with
        {
            Id = Path.GetDirectoryName(Path.GetFullPath(modelPath))!,
            Files = [new LocalModelFile(Path.GetFileName(modelPath), new FileInfo(modelPath).Length, dreamShaper.Checkpoint.Sha256)]
        };
        ImageGenerationGate gate = new();
        NativeStableDiffusionBackend backend = new(AppContext.BaseDirectory, NullLogger<NativeStableDiffusionBackend>.Instance);
        ImageModelLibrary library = new([inPlace], Path.GetDirectoryName(Path.GetFullPath(faceMatching.TrimEnd('\\', '/')))!, new NoNetwork(), gate, NullLogger<ImageModelLibrary>.Instance, backend: backend);

        if (backend.RuntimeProblem is { } problem)
        {
            Assert.Fail(problem);
        }

        Assert.IsTrue(library.IsFaceMatchingReady, "The face-matching folder must hold the adapter and the prepared encoder.");

        return new StableDiffusionImageGenerator(library, backend, gate, NullLogger<StableDiffusionImageGenerator>.Instance);
    }
}
