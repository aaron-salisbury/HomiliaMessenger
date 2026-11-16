using HomiliaMessenger.Business.Modules.ImageGeneration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.ImageGeneration;

/// <summary>
/// Generates images with the active image model in stable-diffusion.cpp, in this process and on the CPU.
/// </summary>
/// <remarks>
/// <para>
/// A Stable Diffusion 1.x model needs about 3.7 GB of memory while it draws (measured with DreamShaper 8 LCM Q8_0), so
/// only one image is generated at a time, idle conversation models are released first, and the model is loaded for each
/// image and released as soon as it is done rather than kept in memory between photos. Loading its weights takes a few
/// of the roughly 18 seconds an image takes on a fast desktop CPU, a cost paid again by "Regenerate" but not worth
/// holding gigabytes of memory for.
/// </para>
/// <para>
/// Drawing runs on a thread of its own, never the UI's. Canceling asks the runtime to stop, which it does between
/// sampling steps; its last stage, decoding, cannot be interrupted, so a cancellation then takes effect when it ends.
/// </para>
/// </remarks>
internal sealed class StableDiffusionImageGenerator : IImageGenerator
{
    private readonly ImageModelLibrary _library;
    private readonly IStableDiffusionBackend _backend;
    private readonly ImageGenerationGate _gate;
    private readonly Func<Func<Task>, Task>? _withoutConversationModelsAsync;
    private readonly ILogger<StableDiffusionImageGenerator> _logger;

    /// <param name="withoutConversationModelsAsync">
    /// Runs the drawing with conversation models released, keeping them from loading again until the image model is.
    /// </param>
    public StableDiffusionImageGenerator(ImageModelLibrary library, IStableDiffusionBackend backend, ImageGenerationGate gate, ILogger<StableDiffusionImageGenerator> logger, Func<Func<Task>, Task>? withoutConversationModelsAsync = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _withoutConversationModelsAsync = withoutConversationModelsAsync;
    }

    public ImageGeneratorAvailability GetAvailability()
    {
        if (_backend.RuntimeProblem is { } problem)
        {
            return new ImageGeneratorAvailability(false, null, false, problem);
        }

        return _library.Active is { } model
            ? new ImageGeneratorAvailability(true, model.DisplayName, true, null)
            : new ImageGeneratorAvailability(false, null, true, "Download an image model in Settings to generate photos.");
    }

    public async Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, IProgress<ImageGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return ImageGenerationResult.Failed("There is nothing to draw: the prompt is empty.");
        }

        if (GetAvailability() is { IsReady: false } availability)
        {
            return ImageGenerationResult.Unavailable(availability.Problem ?? "Image generation is not available.");
        }

        if (!_gate.TryEnter())
        {
            return ImageGenerationResult.Busy;
        }

        try
        {
            if (_library.Active is not { } model)
            {
                return ImageGenerationResult.Unavailable("Download an image model in Settings to generate photos.");
            }

            if (model.Settings.Problem() is { } settingsProblem)
            {
                return ImageGenerationResult.Failed($"{model.DisplayName}'s settings cannot be used: {settingsProblem}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ImageGenerationProgress(ImageGenerationStage.Loading, 0, 0));

            string path = _library.CheckpointPath(model);

            if (!File.Exists(path))
            {
                return ImageGenerationResult.Unavailable($"{model.DisplayName}'s file is missing. Download it again in Settings.");
            }

            GeneratedImage? image = null;

            async Task DrawAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                image = await Task.Factory.StartNew(
                    () => Draw(path, model.Settings, request, progress, cancellationToken),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).ConfigureAwait(false);
            }

            await (_withoutConversationModelsAsync?.Invoke(DrawAsync) ?? DrawAsync()).ConfigureAwait(false);

            if (image is null)
            {
                return ImageGenerationResult.Failed("Could not generate the photo.");
            }

            _logger.LogInformation("Generated a {Width}×{Height} image with {ModelId}.", image.Width, image.Height, model.Id);

            return ImageGenerationResult.Success(PngEncoder.Encode(image));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Image generation was canceled.");
            return ImageGenerationResult.Canceled;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or OutOfMemoryException or ArgumentException)
        {
            _logger.LogError(error, "Image generation failed.");
            return ImageGenerationResult.Failed(error is OutOfMemoryException
                ? "There was not enough memory to generate the photo. Close other programs and try again."
                : "Could not generate the photo: " + error.Message);
        }
        finally
        {
            _gate.Exit();
        }
    }

    /// <summary>
    /// Loads the model, draws, and releases the model, on the calling (background) thread.
    /// </summary>
    private GeneratedImage Draw(string path, ImageGenerationSettings settings, ImageGenerationRequest request, IProgress<ImageGenerationProgress>? progress, CancellationToken cancellationToken)
    {
        using IStableDiffusionContext context = _backend.Open(path);

        // Disposed before the context, waiting for a cancellation in progress, so it never reaches a released context.
        using CancellationTokenRegistration registration = cancellationToken.Register(context.Cancel);
        cancellationToken.ThrowIfCancellationRequested();

        return context.Generate(request.Prompt, settings, request.Seed, (stage, completed, total) =>
        {
            progress?.Report(new ImageGenerationProgress(stage, completed, total));
        });
    }
}
