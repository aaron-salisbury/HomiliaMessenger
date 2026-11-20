using HomiliaMessenger.Business.Modules.ImageGeneration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.ImageGeneration;

/// <summary>
/// Generates contact photos and selfies with the image model chosen for each, in stable-diffusion.cpp, in this process:
/// on the processor for processor models such as DreamShaper 8 LCM, and on the preferred graphics card for graphics card
/// models such as FLUX.2 [klein] 4B.
/// </summary>
/// <remarks>
/// <para>
/// A Stable Diffusion 1.x model needs about 3.7 GB of memory while it draws (measured with DreamShaper 8 LCM Q8_0), and
/// about 4.5 GB with face matching for a selfie; a graphics card model needs about 8 GB of graphics memory, or moves part
/// of itself to the computer's memory on a smaller card. So only one image is generated at a time, idle conversation
/// models are released first, and the model is loaded for each image and released as soon as it is done rather than kept
/// in memory between images. Loading its weights takes a few of the seconds an image takes, a cost paid again by
/// "Generate another" but not worth holding gigabytes of memory for.
/// </para>
/// <para>
/// Drawing runs on a thread of its own, never the UI's. Canceling asks the runtime to stop, which it does between
/// sampling steps; its last stage, decoding, cannot be interrupted, so a cancellation then takes effect when it ends.
/// </para>
/// <para>
/// Prompts are written for how the model reads them (see <see cref="ImagePromptStyle"/>), with the trigger words of the
/// LoRAs in use first. Selfies keep the face with face matching for Stable Diffusion models, or by drawing from the
/// contact's photo as a reference image for FLUX.2 [klein]. Selfies draw one person: Stable Diffusion 1.x mixes up the
/// features of two in one picture, and face matching takes one face.
/// </para>
/// </remarks>
internal sealed class StableDiffusionImageGenerator : IImageGenerator, ISelfieImageGenerator
{
    /// <summary>
    /// How many people a selfie shows.
    /// </summary>
    internal const int MAX_SELFIE_SUBJECTS = 1;

    private const string NO_GPU = "needs a graphics card, and none the image generation runtime can use was found. Choose a processor model in Settings instead.";

    private readonly ImageModelLibrary _library;
    private readonly IStableDiffusionBackend _backend;
    private readonly ImageGenerationGate _gate;
    private readonly Func<Func<Task>, Task>? _withoutConversationModelsAsync;
    private readonly IImageLoraLibrary? _loras;
    private readonly ILogger<StableDiffusionImageGenerator> _logger;

    /// <param name="withoutConversationModelsAsync">
    /// Runs the drawing with conversation models released, keeping them from loading again until the image model is.
    /// </param>
    /// <param name="loras">The LoRAs the user turned on, applied to models they fit.</param>
    public StableDiffusionImageGenerator(ImageModelLibrary library, IStableDiffusionBackend backend, ImageGenerationGate gate, ILogger<StableDiffusionImageGenerator> logger, Func<Func<Task>, Task>? withoutConversationModelsAsync = null, IImageLoraLibrary? loras = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _withoutConversationModelsAsync = withoutConversationModelsAsync;
        _loras = loras;
    }

    public ImageGeneratorAvailability GetAvailability()
    {
        if (_backend.RuntimeProblem is { } problem)
        {
            return new ImageGeneratorAvailability(false, null, false, problem);
        }

        if (_library.Active is not { } model)
        {
            return new ImageGeneratorAvailability(false, null, true, "Download an image model in Settings to generate photos.");
        }

        return DeviceFor(model) is null
            ? new ImageGeneratorAvailability(false, model.DisplayName, true, $"{model.DisplayName} {NO_GPU}")
            : new ImageGeneratorAvailability(true, model.DisplayName, true, null);
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

        if (!_library.IsSelfieReady(model))
        {
            return new SelfieGeneratorAvailability(false, model.DisplayName, MAX_SELFIE_SUBJECTS, true, "Download face matching in Settings, under Selfies, to ask for selfies.");
        }

        return DeviceFor(model) is null
            ? new SelfieGeneratorAvailability(false, model.DisplayName, MAX_SELFIE_SUBJECTS, true, $"{model.DisplayName} {NO_GPU}")
            : new SelfieGeneratorAvailability(true, model.DisplayName, MAX_SELFIE_SUBJECTS, true, null);
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

        return DrawAsync(ImageModelPurpose.Portraits, (model, loras) =>
        {
            string prompt = LoraTriggers.Prepend(request.Prompt, loras, model.Style);
            _logger.LogDebug("Photo prompt: {Prompt}", prompt);
            return (new Drawing(prompt, request.Seed, Loras: loras), null);
        }, "photo", progress, cancellationToken);
    }

    public SelfiePrompt ComposePrompt(SelfieImageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Prompt is { } prompt)
        {
            return prompt;
        }

        if (request.Subjects.Count == 0)
        {
            return new SelfiePrompt(string.Empty, string.Empty);
        }

        ImageModelDefinition? model = _library.ActiveFor(ImageModelPurpose.Selfies);
        ImagePromptStyle style = model?.Style ?? ImagePromptStyle.Weighted;
        SelfiePrompt composed = style == ImagePromptStyle.Plain
            ? PlainSelfiePromptComposer.Compose(request.Subjects[0], request.Scene)
            : SelfiePromptComposer.Compose(request.Subjects[0], request.Scene);
        bool negative = model?.SettingsFor(ImageModelPurpose.Selfies).SupportsNegativePrompt ?? true;
        IReadOnlyList<ImageLoraUse> loras = model is null || _loras is null ? [] : _loras.InUse(model.Family, ImageModelPurpose.Selfies);

        return composed with
        {
            Prompt = LoraTriggers.Prepend(composed.Prompt, loras, style),
            NegativePrompt = negative ? composed.NegativePrompt : string.Empty,
            Style = style,
            SupportsNegativePrompt = negative
        };
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
        bool fromReference = _library.ActiveFor(ImageModelPurpose.Selfies)?.FaceSource == SelfieFaceSource.ReferenceImage;
        GeneratedImage face;

        // Face matching reads the face from the photo's centered square; a reference image is the whole photo.
        try
        {
            face = fromReference ? FaceReference.WholePhoto(subject.Photo) : FaceReference.FromPhoto(subject.Photo);
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

        return DrawAsync(ImageModelPurpose.Selfies, (model, loras) =>
        {
            _logger.LogDebug("Selfie prompt: {Prompt} Negative: {Negative}", prompt.Prompt, prompt.NegativePrompt);
            string? negative = string.IsNullOrEmpty(prompt.NegativePrompt) ? null : prompt.NegativePrompt;

            if (model.FaceSource == SelfieFaceSource.ReferenceImage != fromReference)
            {
                throw new InvalidOperationException("The selfie model changed while the selfie was being prepared. Try again.");
            }

            return fromReference
                ? (new Drawing(prompt.Prompt, request.Seed, negative, References: [face], Loras: loras), null)
                : (new Drawing(prompt.Prompt, request.Seed, negative, face, FaceMatchingCatalog.StrengthFor(model.Family), Loras: loras), _library.FaceMatchingPathsFor(model.Family));
        }, "selfie", progress, cancellationToken);
    }

    /// <summary>
    /// The device <paramref name="model"/> draws on: the processor, or the preferred graphics card; <see langword="null"/>
    /// for a graphics card model when there is none.
    /// </summary>
    private string? DeviceFor(ImageModelDefinition model)
    {
        return model.Processor == ImageProcessor.Gpu ? _backend.GpuDevices().FirstOrDefault()?.Name : ImageModelLoad.CPU;
    }

    /// <summary>
    /// Draws with the model chosen for <paramref name="purpose"/>, one image at a time.
    /// </summary>
    /// <param name="prepare">What to draw with the model, given the LoRAs in use with it, and its face matching, if any.</param>
    /// <param name="what">What is drawn, for problems: "photo" or "selfie".</param>
    private async Task<ImageGenerationResult> DrawAsync(ImageModelPurpose purpose, Func<ImageModelDefinition, IReadOnlyList<ImageLoraUse>, (Drawing Drawing, FaceMatchingFiles? FaceMatching)> prepare, string what, IProgress<ImageGenerationProgress>? progress, CancellationToken cancellationToken)
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

            if (DeviceFor(model) is not { } device)
            {
                return ImageGenerationResult.Unavailable($"{model.DisplayName} {NO_GPU}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ImageGenerationProgress(ImageGenerationStage.Loading, 0, 0));

            IReadOnlyList<ImageLoraUse> loras = _loras?.InUse(model.Family, purpose) ?? [];
            (Drawing drawing, FaceMatchingFiles? faceMatching) = prepare(model, loras);
            ImageModelPaths paths = _library.PathsFor(model);
            IEnumerable<string> files = paths.All.Concat(faceMatching is null ? [] : [faceMatching.ImageEncoderPath, faceMatching.AdapterPath]);

            if (!files.All(File.Exists))
            {
                return ImageGenerationResult.Unavailable($"{model.DisplayName}'s files are missing. Download it again in Settings.");
            }

            ImageModelLoad load = new(paths, device, faceMatching);
            GeneratedImage? image = null;

            async Task DrawInBackgroundAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                image = await Task.Factory.StartNew(
                    () => Draw(load, settings, drawing, progress, cancellationToken),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).ConfigureAwait(false);
            }

            await (_withoutConversationModelsAsync?.Invoke(DrawInBackgroundAsync) ?? DrawInBackgroundAsync()).ConfigureAwait(false);

            if (image is null)
            {
                return ImageGenerationResult.Failed($"Could not generate the {what}.");
            }

            _logger.LogInformation("Generated a {Width}×{Height} {What} with {ModelId} on {Device}{Loras}.", image.Width, image.Height, what, model.Id, device,
                loras.Count == 0 ? string.Empty : " with LoRAs " + string.Join(", ", loras.Select(lora => $"{lora.Name} ({lora.Strength:0.##})")));

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
    private GeneratedImage Draw(ImageModelLoad load, ImageGenerationSettings settings, Drawing drawing, IProgress<ImageGenerationProgress>? progress, CancellationToken cancellationToken)
    {
        using IStableDiffusionContext context = _backend.Open(load);

        // Disposed before the context, waiting for a cancellation in progress, so it never reaches a released context.
        using CancellationTokenRegistration registration = cancellationToken.Register(context.Cancel);
        cancellationToken.ThrowIfCancellationRequested();

        return context.Generate(drawing, settings, (stage, completed, total) =>
        {
            progress?.Report(new ImageGenerationProgress(stage, completed, total));
        });
    }
}
