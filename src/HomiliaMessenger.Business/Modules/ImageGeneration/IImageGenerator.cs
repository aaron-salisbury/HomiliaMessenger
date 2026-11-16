using System;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.ImageGeneration;

/// <summary>
/// How a model turns a prompt into an image. Models differ: a few-step distilled model such as DreamShaper 8 LCM needs a
/// matching sampler and very little guidance, while an ordinary checkpoint needs many more steps, so every model records
/// its own settings rather than sharing one recipe.
/// </summary>
/// <param name="Sampler">The sampling method, by the runtime's name for it, such as <c>lcm</c> or <c>euler_a</c>.</param>
/// <param name="Scheduler">
/// The noise schedule, by the runtime's name, or <see langword="null"/> for the one the runtime pairs with the sampler for
/// this model.
/// </param>
/// <param name="Steps">How many sampling steps to take.</param>
/// <param name="GuidanceScale">Classifier-free guidance (CFG); 1 means the prompt alone, without a negative prompt.</param>
public sealed record ImageGenerationSettings(int Width, int Height, string Sampler, string? Scheduler, int Steps, float GuidanceScale)
{
    /// <summary>
    /// The samplers the integrated runtime offers for Stable Diffusion 1.x models, by its names for them.
    /// </summary>
    public static readonly string[] SAMPLERS = ["euler_a", "euler", "heun", "dpm2", "dpm++2s_a", "dpm++2m", "dpm++2mv2", "ipndm", "ipndm_v", "lcm", "ddim_trailing", "tcd"];

    public const int MIN_SIZE = 256;
    public const int MAX_SIZE = 1024;
    public const int MAX_STEPS = 100;
    public const float MAX_GUIDANCE_SCALE = 30;

    /// <summary>
    /// Why these settings cannot be used, or <see langword="null"/> when they can.
    /// </summary>
    public string? Problem()
    {
        if (Width is < MIN_SIZE or > MAX_SIZE || Height is < MIN_SIZE or > MAX_SIZE || Width % 64 != 0 || Height % 64 != 0)
        {
            return $"Width and height must be multiples of 64 from {MIN_SIZE} to {MAX_SIZE}.";
        }

        if (Array.IndexOf(SAMPLERS, Sampler) < 0)
        {
            return $"\"{Sampler}\" is not a sampler this app can use.";
        }

        if (Steps is < 1 or > MAX_STEPS)
        {
            return $"Steps must be from 1 to {MAX_STEPS}.";
        }

        if (!float.IsFinite(GuidanceScale) || GuidanceScale < 1 || GuidanceScale > MAX_GUIDANCE_SCALE)
        {
            return $"Guidance (CFG) must be from 1 to {MAX_GUIDANCE_SCALE}.";
        }

        return null;
    }
}

/// <param name="Prompt">What to draw, as the model reads it.</param>
/// <param name="Seed">Chooses one of the many images the prompt could produce; the same seed and prompt repeat an image.</param>
public sealed record ImageGenerationRequest(string Prompt, long Seed);

public enum ImageGenerationStage
{
    /// <summary>
    /// Waiting for memory: releasing an idle chat model, or reading the image model's file.
    /// </summary>
    Loading,

    /// <summary>
    /// Reading the prompt.
    /// </summary>
    Preparing,

    /// <summary>
    /// Drawing; <see cref="ImageGenerationProgress.Completed"/> of <see cref="ImageGenerationProgress.Total"/> steps.
    /// </summary>
    Sampling,

    /// <summary>
    /// Turning the drawing into pixels, the last stage.
    /// </summary>
    Decoding
}

/// <param name="Total">Zero when the stage's length is not known.</param>
public sealed record ImageGenerationProgress(ImageGenerationStage Stage, long Completed, long Total);

public enum ImageGenerationStatus
{
    Success,

    /// <summary>
    /// No image model is ready, or image generation is not included for this system.
    /// </summary>
    Unavailable,

    /// <summary>
    /// Another image is being generated; only one is made at a time, to stay within the computer's memory.
    /// </summary>
    Busy,
    Canceled,
    Failed
}

/// <summary>
/// The outcome of generating an image. On success, <see cref="Png"/> holds the image as a PNG file.
/// </summary>
public sealed record ImageGenerationResult(ImageGenerationStatus Status, byte[]? Png, string? Problem)
{
    public bool IsSuccessful => Status == ImageGenerationStatus.Success && Png is not null;

    public static ImageGenerationResult Success(byte[] png)
    {
        return new(ImageGenerationStatus.Success, png, null);
    }

    public static ImageGenerationResult Unavailable(string problem)
    {
        return new(ImageGenerationStatus.Unavailable, null, problem);
    }

    public static ImageGenerationResult Busy { get; } = new(ImageGenerationStatus.Busy, null, "Another image is being generated. Try again when it is done.");

    public static ImageGenerationResult Canceled { get; } = new(ImageGenerationStatus.Canceled, null, null);

    public static ImageGenerationResult Failed(string problem)
    {
        return new(ImageGenerationStatus.Failed, null, problem);
    }
}

/// <summary>
/// Whether images can be generated now, and if not, why.
/// </summary>
/// <param name="ModelName">The image model that would be used, when one is ready.</param>
/// <param name="IsRuntimeIncluded">
/// Whether this build of the app includes image generation for this system; when it does not, downloading a model will
/// not help.
/// </param>
public sealed record ImageGeneratorAvailability(bool IsReady, string? ModelName, bool IsRuntimeIncluded, string? Problem);

/// <summary>
/// Capability to draw an image from a text prompt with a model on this computer. Separate from conversation: image models
/// are never contacts' agents. One image is generated at a time.
/// </summary>
public interface IImageGenerator
{
    ImageGeneratorAvailability GetAvailability();

    /// <summary>
    /// Generates an image with the active image model and its settings. Canceling stops the generation at the next point
    /// the runtime checks, between sampling steps; the last stage, decoding, finishes before it stops.
    /// </summary>
    Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, IProgress<ImageGenerationProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Used when no image generation provider is registered.
/// </summary>
internal sealed class UnavailableImageGenerator : IImageGenerator
{
    private const string PROBLEM = "Image generation is not available in this app.";

    public ImageGeneratorAvailability GetAvailability()
    {
        return new ImageGeneratorAvailability(false, null, false, PROBLEM);
    }

    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, IProgress<ImageGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ImageGenerationResult.Unavailable(PROBLEM));
    }
}
