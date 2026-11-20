using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.ImageGeneration;

/// <summary>
/// How a LoRA is used: whether at all, how strongly, the words that call it up, and for what.
/// </summary>
/// <param name="Strength">How strongly it changes the model, usually from 0.5 to 1.</param>
/// <param name="TriggerWords">Words added to the start of every prompt it is used with, which many LoRAs need to show.</param>
/// <param name="Purposes">Whether it is used for contact photos, selfies, or both.</param>
public sealed record ImageLoraSettings(bool IsEnabled, float Strength, string TriggerWords, ImageModelPurpose Purposes)
{
    public const float MIN_STRENGTH = 0f;
    public const float MAX_STRENGTH = 2f;

    /// <summary>
    /// The strength a LoRA starts at when it suggests none: a little below full, which suits the few-step models this app
    /// uses better than 1.
    /// </summary>
    public const float DEFAULT_STRENGTH = 0.8f;

    public const int MAX_TRIGGER_WORDS_LENGTH = 300;

    /// <summary>
    /// Why these settings cannot be saved, or <see langword="null"/> when they can.
    /// </summary>
    public string? Problem()
    {
        if (!float.IsFinite(Strength) || Strength < MIN_STRENGTH || Strength > MAX_STRENGTH)
        {
            return $"Strength must be from {MIN_STRENGTH:0} to {MAX_STRENGTH:0}.";
        }

        if (TriggerWords is null || TriggerWords.Length > MAX_TRIGGER_WORDS_LENGTH)
        {
            return $"Keep trigger words to {MAX_TRIGGER_WORDS_LENGTH} characters or fewer.";
        }

        return null;
    }
}

/// <summary>
/// A LoRA file found in the LoRA folder: a small add-on that changes what a model draws (a style, a character, a kind of
/// clothing), trained for one base model.
/// </summary>
/// <param name="Name">Its path within the folder, which identifies it.</param>
/// <param name="Base">The base model it was trained for, as its file shows; it is used only with models of that family.</param>
/// <param name="BaseDescription">The base in words, such as "SDXL" or "FLUX.1", including bases this app does not run.</param>
/// <param name="Note">A caution, such as that it was trained on Pony Diffusion and may suit DreamShaper XL poorly.</param>
/// <param name="SuggestedTriggerWords">Trigger words read from the file or the notes beside it, when any were found.</param>
/// <param name="TriggerWordsSource">Where <see cref="SuggestedTriggerWords"/> came from, such as "its Civitai info".</param>
/// <param name="SuggestedStrength">The strength the notes beside it recommend, when they do.</param>
/// <param name="Settings">How it is used, as last saved, or as suggested when it was never changed.</param>
public sealed record ImageLora(
    string Name,
    string DisplayName,
    long Bytes,
    ImageModelFamily Base,
    string BaseDescription,
    string? Note,
    string? SuggestedTriggerWords,
    string? TriggerWordsSource,
    float? SuggestedStrength,
    ImageLoraSettings Settings)
{
    /// <summary>
    /// Whether it can be used with models of <paramref name="family"/>.
    /// </summary>
    public bool Fits(ImageModelFamily family)
    {
        return Base != ImageModelFamily.Unknown && Base == family;
    }
}

/// <summary>
/// A LoRA in use for one image, as the runtime takes it.
/// </summary>
/// <param name="Path">The file's full path.</param>
public sealed record ImageLoraUse(string Name, string Path, float Strength, string TriggerWords);

/// <summary>
/// Capability to use LoRAs from a folder the user chooses, such as one they already keep for other image tools. Each
/// file's base model is read from the file, and trigger words and strength from it or the notes beside it, so a LoRA is
/// only ever used with a model it was made for.
/// </summary>
public interface IImageLoraLibrary
{
    /// <summary>
    /// The folder LoRAs are read from, or <see langword="null"/> when none was chosen.
    /// </summary>
    string? Folder { get; }

    /// <summary>
    /// Chooses the folder LoRAs are read from, or none. Returns why it cannot be used, or <see langword="null"/>.
    /// </summary>
    string? SetFolder(string? folder);

    /// <summary>
    /// Reads every LoRA in the folder and its subfolders: the header of each <c>.safetensors</c> file (not its weights),
    /// and the Civitai or Stable Diffusion web UI notes beside it.
    /// </summary>
    Task<IReadOnlyList<ImageLora>> ScanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves how a LoRA is used. Returns why the settings cannot be saved, or <see langword="null"/>.
    /// </summary>
    string? Save(string name, ImageLoraSettings settings);

    /// <summary>
    /// The LoRAs to draw with: those turned on for <paramref name="purpose"/> that fit models of <paramref name="family"/>,
    /// and are still in the folder.
    /// </summary>
    IReadOnlyList<ImageLoraUse> InUse(ImageModelFamily family, ImageModelPurpose purpose);
}
