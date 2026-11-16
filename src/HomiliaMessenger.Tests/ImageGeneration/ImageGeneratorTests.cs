using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Integrations.ImageGeneration;
using HomiliaMessenger.Integrations.LocalModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.ImageGeneration;

/// <summary>
/// The image generator's lifecycle around the native runtime: availability, one image at a time, cancellation, failures,
/// and releasing every loaded model. The runtime is a fake; <see cref="RealImageModelTests"/> uses the real one.
/// </summary>
[TestClass]
public sealed class ImageGeneratorTests
{
    private static readonly ImageGenerationSettings SETTINGS = new(256, 256, "lcm", null, 4, 1f);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Generate_WithoutADownloadedModel_IsUnavailableAndLoadsNothing()
    {
        using TestFolder folder = new();
        FakeBackend backend = new();
        StableDiffusionImageGenerator generator = CreateGenerator(folder.Path, backend, downloaded: false);

        ImageGeneratorAvailability availability = generator.GetAvailability();
        ImageGenerationResult result = await generator.GenerateAsync(new ImageGenerationRequest("A man.", 1), cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(availability.IsReady);
        Assert.IsTrue(availability.IsRuntimeIncluded);
        Assert.Contains("Settings", availability.Problem!);
        Assert.AreEqual(ImageGenerationStatus.Unavailable, result.Status);
        Assert.AreEqual(0, backend.Opened);
    }

    [TestMethod]
    public async Task Generate_WithoutTheRuntime_SaysSoRatherThanAskingForAModel()
    {
        using TestFolder folder = new();
        FakeBackend backend = new() { RuntimeProblem = "This build does not include image generation." };
        StableDiffusionImageGenerator generator = CreateGenerator(folder.Path, backend);

        ImageGeneratorAvailability availability = generator.GetAvailability();
        ImageGenerationResult result = await generator.GenerateAsync(new ImageGenerationRequest("A man.", 1), cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(availability.IsReady);
        Assert.IsFalse(availability.IsRuntimeIncluded);
        Assert.AreEqual(ImageGenerationStatus.Unavailable, result.Status);
        Assert.AreEqual(backend.RuntimeProblem, result.Problem);
        Assert.AreEqual(0, backend.Opened);
    }

    [TestMethod]
    public async Task Generate_DrawsWithTheModelsSettingsAndReleasesTheModel()
    {
        using TestFolder folder = new();
        FakeBackend backend = new();
        int released = 0;
        int disposedWhileHeld = -1;
        StableDiffusionImageGenerator generator = CreateGenerator(folder.Path, backend, withoutConversationModels: async draw =>
        {
            Assert.AreEqual(0, backend.Opened, "Conversation models are released before the image model is loaded.");
            released++;
            await draw();
            disposedWhileHeld = backend.Disposed;
        });
        List<ImageGenerationProgress> updates = [];

        ImageGenerationResult result = await generator.GenerateAsync(new ImageGenerationRequest("Older man with a white beard.", 1234), new SynchronousProgress(updates), TestContext.CancellationToken);

        Assert.AreEqual(ImageGenerationStatus.Success, result.Status, result.Problem);
        (int width, int height, byte[] pixels) = PngTestDecoder.Decode(result.Png!);
        Assert.AreEqual(256, width);
        Assert.AreEqual(256, height);
        Assert.AreEqual((byte)(1234 % 256), pixels[0]);
        Assert.AreEqual(("Older man with a white beard.", SETTINGS, 1234L), backend.Requests[0]);
        Assert.AreEqual(1, released);
        Assert.AreEqual(1, disposedWhileHeld, "Conversation models stay released until the image model is.");
        Assert.AreEqual(1, backend.Opened);
        Assert.AreEqual(1, backend.Disposed);
        Assert.AreEqual(ImageGenerationStage.Loading, updates[0].Stage);
        Assert.Contains(update => update.Stage == ImageGenerationStage.Sampling && update.Completed == 4 && update.Total == 4, updates);
        Assert.AreEqual(ImageGenerationStage.Decoding, updates[^1].Stage);
    }

    [TestMethod]
    public async Task Generate_MakesOneImageAtATime()
    {
        using TestFolder folder = new();
        using ManualResetEventSlim hold = new();
        FakeBackend backend = new() { Hold = hold };
        StableDiffusionImageGenerator generator = CreateGenerator(folder.Path, backend);

        Task<ImageGenerationResult> first = generator.GenerateAsync(new ImageGenerationRequest("First.", 1), cancellationToken: TestContext.CancellationToken);
        Assert.IsTrue(backend.Drawing.Wait(TimeSpan.FromSeconds(10), TestContext.CancellationToken));
        ImageGenerationResult second = await generator.GenerateAsync(new ImageGenerationRequest("Second.", 2), cancellationToken: TestContext.CancellationToken);
        hold.Set();
        ImageGenerationResult firstResult = await first;
        ImageGenerationResult third = await generator.GenerateAsync(new ImageGenerationRequest("Third.", 3), cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(ImageGenerationStatus.Busy, second.Status);
        Assert.AreEqual(ImageGenerationStatus.Success, firstResult.Status);
        Assert.AreEqual(ImageGenerationStatus.Success, third.Status);
        Assert.AreEqual(2, backend.Opened);
    }

    [TestMethod]
    public async Task Generate_CanceledWhileDrawing_StopsAndReleasesTheModel()
    {
        using TestFolder folder = new();
        using ManualResetEventSlim hold = new();
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        FakeBackend backend = new() { Hold = hold };
        StableDiffusionImageGenerator generator = CreateGenerator(folder.Path, backend);

        Task<ImageGenerationResult> generating = generator.GenerateAsync(new ImageGenerationRequest("A woman.", 5), cancellationToken: cancel.Token);
        Assert.IsTrue(backend.Drawing.Wait(TimeSpan.FromSeconds(10), TestContext.CancellationToken));
        cancel.Cancel();
        ImageGenerationResult result = await generating;
        int releasedAfterCancel = backend.Disposed;
        backend.Hold = null;
        ImageGenerationResult next = await generator.GenerateAsync(new ImageGenerationRequest("A woman.", 6), cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(ImageGenerationStatus.Canceled, result.Status);
        Assert.IsNull(result.Png);
        Assert.AreEqual(1, releasedAfterCancel);
        Assert.AreEqual(ImageGenerationStatus.Success, next.Status);
    }

    [TestMethod]
    public async Task Generate_CanceledBeforeItStarts_LoadsNothing()
    {
        using TestFolder folder = new();
        FakeBackend backend = new();
        StableDiffusionImageGenerator generator = CreateGenerator(folder.Path, backend);

        ImageGenerationResult result = await generator.GenerateAsync(new ImageGenerationRequest("A man.", 1), cancellationToken: new CancellationToken(canceled: true));

        Assert.AreEqual(ImageGenerationStatus.Canceled, result.Status);
        Assert.AreEqual(0, backend.Opened);
    }

    [TestMethod]
    public async Task Generate_WhenTheModelCannotLoad_FailsAndLetsTheNextOneTry()
    {
        using TestFolder folder = new();
        FakeBackend backend = new() { OpenFailure = new InvalidOperationException("The image model could not be loaded.") };
        StableDiffusionImageGenerator generator = CreateGenerator(folder.Path, backend);

        ImageGenerationResult failed = await generator.GenerateAsync(new ImageGenerationRequest("A man.", 1), cancellationToken: TestContext.CancellationToken);
        backend.OpenFailure = null;
        ImageGenerationResult next = await generator.GenerateAsync(new ImageGenerationRequest("A man.", 1), cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(ImageGenerationStatus.Failed, failed.Status);
        Assert.Contains("could not be loaded", failed.Problem!);
        Assert.AreEqual(ImageGenerationStatus.Success, next.Status);
    }

    [TestMethod]
    public async Task Generate_WhenDrawingFails_ReleasesTheModel()
    {
        using TestFolder folder = new();
        FakeBackend backend = new() { GenerateFailure = new InvalidOperationException("The image could not be generated.") };
        StableDiffusionImageGenerator generator = CreateGenerator(folder.Path, backend);

        ImageGenerationResult result = await generator.GenerateAsync(new ImageGenerationRequest("A man.", 1), cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(ImageGenerationStatus.Failed, result.Status);
        Assert.AreEqual(1, backend.Opened);
        Assert.AreEqual(1, backend.Disposed);
    }

    [TestMethod]
    public async Task Generate_RefusesAnEmptyPromptAndUnusableSettings()
    {
        using TestFolder folder = new();
        FakeBackend backend = new();
        StableDiffusionImageGenerator generator = CreateGenerator(folder.Path, backend);
        StableDiffusionImageGenerator badSettings = CreateGenerator(folder.Path, backend, settings: SETTINGS with { Sampler = "nonsense" });

        ImageGenerationResult empty = await generator.GenerateAsync(new ImageGenerationRequest("  ", 1), cancellationToken: TestContext.CancellationToken);
        ImageGenerationResult bad = await badSettings.GenerateAsync(new ImageGenerationRequest("A man.", 1), cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(ImageGenerationStatus.Failed, empty.Status);
        Assert.AreEqual(ImageGenerationStatus.Failed, bad.Status);
        Assert.Contains("nonsense", bad.Problem!);
        Assert.AreEqual(0, backend.Opened);
    }

    [TestMethod]
    public void Settings_DescribeWhatTheRuntimeCanUse()
    {
        Assert.IsNull(ImageModelCatalog.DREAMSHAPER_SETTINGS.Problem());
        Assert.IsNotNull((SETTINGS with { Width = 500 }).Problem());
        Assert.IsNotNull((SETTINGS with { Steps = 0 }).Problem());
        Assert.IsNotNull((SETTINGS with { GuidanceScale = 0.5f }).Problem());
        Assert.IsNotNull((SETTINGS with { GuidanceScale = float.NaN }).Problem());
        Assert.IsNotNull((SETTINGS with { Sampler = "flux" }).Problem());
    }

    [TestMethod]
    public void PngEncoder_WritesTheImageItWasGiven()
    {
        byte[] pixels = new byte[3 * 4 * 3];

        for (int index = 0; index < pixels.Length; index++)
        {
            pixels[index] = (byte)(index * 7);
        }

        byte[] png = PngEncoder.Encode(new GeneratedImage(4, 3, 3, pixels));
        (int width, int height, byte[] decoded) = PngTestDecoder.Decode(png);

        Assert.AreEqual(4, width);
        Assert.AreEqual(3, height);
        Assert.AreSequenceEqual(pixels, decoded);
        Assert.ThrowsExactly<ArgumentException>(() => PngEncoder.Encode(new GeneratedImage(4, 3, 3, new byte[5])));
    }

    [TestMethod]
    public void NativeBackend_WithoutTheRuntimeFiles_SaysItIsNotIncludedWithoutLoadingAnything()
    {
        using TestFolder folder = new();
        NativeStableDiffusionBackend backend = new(folder.Path, NullLogger<NativeStableDiffusionBackend>.Instance);

        Assert.IsNotNull(backend.RuntimeProblem);
        Assert.ThrowsExactly<InvalidOperationException>(() => backend.Open(Path.Combine(folder.Path, "model.gguf")));
    }

    /// <summary>
    /// A generator over a library holding one small "model" file, downloaded unless told otherwise.
    /// </summary>
    private static StableDiffusionImageGenerator CreateGenerator(string folder, FakeBackend backend, bool downloaded = true, Func<Func<Task>, Task>? withoutConversationModels = null, ImageGenerationSettings? settings = null)
    {
        LocalModelFile file = new("test.gguf", 4, "00");
        ImageModelDefinition model = new("test-model", "Test model", "A test model.", "MIT", null, "https://example.test/", [file], settings ?? SETTINGS, true);

        if (downloaded)
        {
            Directory.CreateDirectory(Path.Combine(folder, model.Id));
            File.WriteAllBytes(Path.Combine(folder, model.Id, file.LocalName), [1, 2, 3, 4]);
        }

        ImageGenerationGate gate = new();
        ImageModelLibrary library = new([model], folder, new NoNetwork(), gate, NullLogger<ImageModelLibrary>.Instance);

        return new StableDiffusionImageGenerator(library, backend, gate, NullLogger<StableDiffusionImageGenerator>.Instance, withoutConversationModels);
    }
}

internal sealed class TestFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"HomiliaMessenger-images-{Guid.NewGuid():N}");

    public TestFolder()
    {
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        Directory.Delete(Path, recursive: true);
    }
}
