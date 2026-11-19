using HomiliaMessenger.Business.Modules.ImageGeneration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.ImageGeneration;

/// <summary>
/// Generates contact photos and selfies with the image model chosen for each, in stable-diffusion.cpp, in this process and
/// on the CPU.
/// </summary>
/// <remarks>
/// <para>
/// A Stable Diffusion 1.x model needs about 3.7 GB of memory while it draws (measured with DreamShaper 8 LCM Q8_0), and
/// about 4.5 GB with face matching for a selfie, so only one image is generated at a time, idle conversation models are
/// released first, and the model is loaded for each image and released as soon as it is done rather than kept in memory
/// between images. Loading its weights takes a few of the seconds an image takes, a cost paid again by "Generate another"
/// but not worth holding gigabytes of memory for.
/// </para>
/// <para>
/// Drawing runs on a thread of its own, never the UI's. Canceling asks the runtime to stop, which it does between
/// sampling steps; its last stage, decoding, cannot be interrupted, so a cancellation then takes effect when it ends.
/// </para>
/// <para>
/// Selfies draw one person: Stable Diffusion 1.x mixes up the features of two in one picture, and face matching takes
/// one face. A model that draws several people could take more of the request's subjects.
/// </para>
/// </remarks>
internal sealed class StableDiffusionImageGenerator : IImageGenerator, ISelfieImageGenerator
{
    /// <summary>
    /// How many people a selfie shows with a Stable Diffusion 1.x model.
    /// </summary>
    internal const int MAX_SELFIE_SUBJECTS = 1;

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

    SelfieGeneratorAvailability ISelfieImageGenerator.GetAvailability()
    {
        return SelfieAvailability();
    }

    internal SelfieGeneratorAvailability SelfieAvailability()
    {
        if (_backend.RuntimeProblem is { } problem)
        {
            return new SelfieGeneratorAvailability(false, null, 0, false, problem);
        }

        if (_library.ActiveFor(ImageModelPurpose.Selfies) is not { } model)
        {
            return new SelfieGeneratorAvailability(false, null, MAX_SELFIE_SUBJECTS, true, "Download a selfie model in Settings to ask for selfies.");
        }

        return _library.IsFaceMatchingReady
            ? new SelfieGeneratorAvailability(true, model.DisplayName, MAX_SELFIE_SUBJECTS, true, null)
            : new SelfieGeneratorAvailability(false, model.DisplayName, MAX_SELFIE_SUBJECTS, true, "Download face matching in Settings, under Selfies, to ask for selfies.");
    }

    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, IProgress<ImageGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return Task.FromResult(ImageGenerationResult.Failed("There is nothing to draw: the prompt is empty."));
        }

        if (GetAvailability() is { IsReady: false } availability)
        {
            return Task.FromResult(ImageGenerationResult.Unavailable(availability.Problem ?? "Image generation is not available."));
        }

        return DrawAsync(ImageModelPurpose.Portraits, new Drawing(request.Prompt, request.Seed), null, "photo", progress, cancellationToken);
    }

    public SelfiePrompt ComposePrompt(SelfieImageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Prompt is { } prompt)
        {
            return prompt;
        }

        return request.Subjects.Count == 0 ? new SelfiePrompt(string.Empty, string.Empty) : SelfiePromptComposer.Compose(request.Subjects[0], request.Scene);
    }

    public Task<ImageGenerationResult> GenerateAsync(SelfieImageRequest request, IProgress<ImageGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Subjects.Count == 0)
        {
            return Task.FromResult(ImageGenerationResult.Failed("There is no one to draw in the selfie."));
        }

        if (SelfieAvailability() is { IsReady: false } availability)
        {
            return Task.FromResult(ImageGenerationResult.Unavailable(availability.Problem ?? "Selfies are not available."));
        }

        SelfieSubject subject = request.Subjects[0];
        GeneratedImage face;

        try
        {
            face = FaceReference.FromPhoto(subject.Photo);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            _logger.LogWarning(error, "A contact's photo could not be read for a selfie.");
            return Task.FromResult(ImageGenerationResult.Failed($"{subject.Name}'s profile photo could not be read for the selfie. Try another photo."));
        }

        SelfiePrompt prompt = ComposePrompt(request);

        if (string.IsNullOrWhiteSpace(prompt.Prompt))
        {
            return Task.FromResult(ImageGenerationResult.Failed("There is nothing to draw: the prompt is empty."));
        }

        _logger.LogDebug("Selfie prompt: {Prompt} Negative: {Negative}", prompt.Prompt, prompt.NegativePrompt);
        Drawing drawing = new(prompt.Prompt, request.Seed, prompt.NegativePrompt, face, FaceMatchingCatalog.STRENGTH);

        return DrawAsync(ImageModelPurpose.Selfies, drawing, _library.FaceMatchingPaths, "selfie", progress, cancellationToken);
    }

    /// <summary>
    /// Draws with the model chosen for <paramref name="purpose"/>, one image at a time.
    /// </summary>
    /// <param name="what">What is drawn, for problems: "photo" or "selfie".</param>
    private async Task<ImageGenerationResult> DrawAsync(ImageModelPurpose purpose, Drawing drawing, FaceMatchingFiles? faceMatching, string what, IProgress<ImageGenerationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!_gate.TryEnter())
        {
            return ImageGenerationResult.Busy;
        }

        try
        {
            if (_library.ActiveFor(purpose) is not { } model)
            {
                return ImageGenerationResult.Unavailable($"Download an image model in Settings to generate a {what}.");
            }

            ImageGenerationSettings settings = model.SettingsFor(purpose);

            if (settings.Problem() is { } settingsProblem)
            {
                return ImageGenerationResult.Failed($"{model.DisplayName}'s settings cannot be used: {settingsProblem}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ImageGenerationProgress(ImageGenerationStage.Loading, 0, 0));

            string path = _library.CheckpointPath(model);

            if (!File.Exists(path) || (faceMatching is not null && (!File.Exists(faceMatching.ImageEncoderPath) || !File.Exists(faceMatching.AdapterPath))))
            {
                return ImageGenerationResult.Unavailable($"{model.DisplayName}'s files are missing. Download it again in Settings.");
            }

            GeneratedImage? image = null;

            async Task DrawInBackgroundAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                image = await Task.Factory.StartNew(
                    () => Draw(path, faceMatching, settings, drawing, progress, cancellationToken),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).ConfigureAwait(false);
            }

            await (_withoutConversationModelsAsync?.Invoke(DrawInBackgroundAsync) ?? DrawInBackgroundAsync()).ConfigureAwait(false);

            if (image is null)
            {
                return ImageGenerationResult.Failed($"Could not generate the {what}.");
            }

            _logger.LogInformation("Generated a {Width}×{Height} {What} with {ModelId}.", image.Width, image.Height, what, model.Id);

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
                ? $"There was not enough memory to generate the {what}. Close other programs and try again."
                : $"Could not generate the {what}: " + error.Message);
        }
        finally
        {
            _gate.Exit();
        }
    }

    /// <summary>
    /// Loads the model, draws, and releases the model, on the calling (background) thread.
    /// </summary>
    private GeneratedImage Draw(string path, FaceMatchingFiles? faceMatching, ImageGenerationSettings settings, Drawing drawing, IProgress<ImageGenerationProgress>? progress, CancellationToken cancellationToken)
    {
        using IStableDiffusionContext context = _backend.Open(path, faceMatching);

        // Disposed before the context, waiting for a cancellation in progress, so it never reaches a released context.
        using CancellationTokenRegistration registration = cancellationToken.Register(context.Cancel);
        cancellationToken.ThrowIfCancellationRequested();

        return context.Generate(drawing, settings, (stage, completed, total) =>
        {
            progress?.Report(new ImageGenerationProgress(stage, completed, total));
        });
    }
}
