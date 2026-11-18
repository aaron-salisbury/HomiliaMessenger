using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.ImageGeneration;

/// <summary>
/// A person in a selfie: their photo, which shows their face, and the words that describe them in this scene.
/// </summary>
/// <param name="Name">Who they are, for logs and problems; never drawn.</param>
/// <param name="Photo">Their profile photo, PNG or JPEG, whose face the selfie keeps.</param>
/// <param name="Appearance">Who they are in words, such as "man in his fifties with a short gray beard and eyeglasses".</param>
/// <param name="Expression">How they look in this selfie, such as "laughing" or "irritated", when known.</param>
/// <param name="Clothing">What they wear in this selfie, when known.</param>
public sealed record SelfieSubject(string Name, byte[] Photo, string Appearance, string? Expression = null, string? Clothing = null);

/// <summary>
/// Where a selfie is taken and what is going on there, in plain words; any part may be unknown.
/// </summary>
/// <param name="Location">Such as "a bus stop on a city street".</param>
/// <param name="Activity">What the person is doing, such as "waiting for the bus".</param>
/// <param name="Props">Things to show beside them, such as "a large trout".</param>
/// <param name="Weather">Such as "pouring rain"; also states like "soaking wet" that the weather leaves them in.</param>
/// <param name="Lighting">The time of day or light, such as "golden hour" or "night, neon lights".</param>
public sealed record SelfieScene(string? Location = null, string? Activity = null, string? Props = null, string? Weather = null, string? Lighting = null)
{
    public static SelfieScene Unknown { get; } = new();
}

/// <param name="Subjects">
/// Who is in the selfie, most important first. A model draws as many as it can (see
/// <see cref="SelfieGeneratorAvailability.MaxSubjects"/>), from the first.
/// </param>
/// <param name="Seed">Chooses one of the many images the request could produce.</param>
public sealed record SelfieImageRequest(IReadOnlyList<SelfieSubject> Subjects, SelfieScene Scene, long Seed);

/// <summary>
/// Whether selfies can be drawn now, and if not, why.
/// </summary>
/// <param name="MaxSubjects">How many people the selfie model can draw in one picture, each looking like their photo.</param>
/// <param name="IsRuntimeIncluded">Whether this build of the app includes image generation for this system.</param>
public sealed record SelfieGeneratorAvailability(bool IsReady, string? ModelName, int MaxSubjects, bool IsRuntimeIncluded, string? Problem);

/// <summary>
/// Capability to draw a selfie of people who look like their photos, in a scene described in words, with a model on this
/// computer. One image is generated at a time, shared with contact photos.
/// </summary>
public interface ISelfieImageGenerator
{
    SelfieGeneratorAvailability GetAvailability();

    /// <summary>
    /// Draws a selfie with the model chosen for selfies. Canceling stops at the next point the runtime checks, as for
    /// contact photos.
    /// </summary>
    Task<ImageGenerationResult> GenerateAsync(SelfieImageRequest request, IProgress<ImageGenerationProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Used when no selfie provider is registered.
/// </summary>
internal sealed class UnavailableSelfieImageGenerator : ISelfieImageGenerator
{
    private const string PROBLEM = "Selfies are not available in this app.";

    public SelfieGeneratorAvailability GetAvailability()
    {
        return new SelfieGeneratorAvailability(false, null, 0, false, PROBLEM);
    }

    public Task<ImageGenerationResult> GenerateAsync(SelfieImageRequest request, IProgress<ImageGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ImageGenerationResult.Unavailable(PROBLEM));
    }
}
