using HomiliaMessenger.Business.Modules.Agents;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.ImageGeneration;

/// <summary>
/// An image model that can run on this computer, kept apart from conversational and embedding models.
/// </summary>
/// <param name="LicenseUrl">Where the model's license can be read, when known.</param>
/// <param name="IsSelected">Whether images are generated with it, once it is downloaded.</param>
/// <param name="IsUserAdded">Whether the user added it from Hugging Face; removing it forgets it too.</param>
/// <param name="IsVerified">
/// Whether the model and its settings were tested with this app's runtime. A model added from Hugging Face is not.
/// </param>
public sealed record ImageModel(
    string Id,
    string DisplayName,
    string Description,
    string License,
    string? LicenseUrl,
    long DownloadBytes,
    bool IsDownloaded,
    bool IsSelected,
    bool IsUserAdded,
    bool IsVerified,
    ImageGenerationSettings Settings);

/// <summary>
/// The kind of model a GGUF file holds, as far as its tensors show.
/// </summary>
public enum ImageModelFamily
{
    /// <summary>
    /// The file's contents could not be read or recognized.
    /// </summary>
    Unknown,

    /// <summary>
    /// Stable Diffusion 1.x and its fine-tunes, such as DreamShaper 8: the only family this app runs.
    /// </summary>
    StableDiffusion1,
    StableDiffusion2,
    StableDiffusionXL,

    /// <summary>
    /// Another image model, such as SD3 or FLUX.
    /// </summary>
    OtherImageModel,

    /// <summary>
    /// A language model, for conversation or embeddings, rather than an image model.
    /// </summary>
    LanguageModel
}

/// <summary>
/// A GGUF file in a Hugging Face repository, with what its header shows about whether this app can generate images
/// with it. The header is read without downloading the model.
/// </summary>
/// <param name="HasTextEncoder">Whether the file includes the text encoder that reads prompts.</param>
/// <param name="HasVae">Whether the file includes the VAE that turns the drawing into pixels.</param>
/// <param name="Quantization">The weight format of most of the model, such as <c>Q8_0</c> or <c>F16</c>.</param>
/// <param name="Problem">Why the file cannot be used, or <see langword="null"/> when it can.</param>
/// <param name="KnownModelId">The built-in model this file is, when its hash matches one.</param>
public sealed record ImageModelFile(
    HuggingFaceFile File,
    ImageModelFamily Family,
    bool HasTextEncoder,
    bool HasVae,
    string? Quantization,
    string? Problem,
    string? KnownModelId = null)
{
    public bool IsSupported => Problem is null && KnownModelId is null;
}

/// <summary>
/// The GGUF files a Hugging Face repository offers, at the revision it was looked up at, with what each one is.
/// </summary>
/// <param name="Revision">The commit the files belong to; downloads are pinned to it so the hashes stay valid.</param>
/// <param name="PipelineTag">What the repository says its model does, such as <c>text-to-image</c>, when it says.</param>
public sealed record ImageModelLookup(HuggingFaceLookupStatus Status, string RepoId, string? Revision, string? License, string? PipelineTag, IReadOnlyList<ImageModelFile> Files, string? Problem)
{
    public string Url => "https://huggingface.co/" + RepoId;

    public static ImageModelLookup Unsuccessful(HuggingFaceLookupStatus status, string repoId, string problem)
    {
        return new(status, repoId, null, null, null, [], problem);
    }
}

/// <summary>
/// Capability to manage the image models that run on this computer and choose the one photos are generated with.
/// </summary>
public interface IImageModelLibrary
{
    IReadOnlyList<ImageModel> List();

    /// <summary>
    /// Downloads and verifies a model. When no model is in use yet, the downloaded one is used.
    /// </summary>
    Task<LocalModelDownloadResult> DownloadAsync(string modelId, IProgress<LocalModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a downloaded model, freeing its disk space; one the user added is forgotten too. When it was in use and
    /// another model is downloaded, that one is used instead.
    /// </summary>
    Task<bool> DeleteAsync(string modelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Chooses the model photos are generated with. Returns <see langword="false"/> for an unknown model.
    /// </summary>
    bool Select(string modelId);

    /// <summary>
    /// Lists the GGUF files in a Hugging Face repository, given as <c>owner/name</c> or its address, and reads each one's
    /// header to tell whether this app can use it. Nothing is downloaded beyond the headers.
    /// </summary>
    Task<ImageModelLookup> LookUpAsync(string repository, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a supported file from a lookup to the models offered, with the settings the user gave for it, ready to
    /// download. Its compatibility is not verified.
    /// </summary>
    Task<LocalModelAddResult> AddAsync(ImageModelLookup lookup, ImageModelFile file, ImageGenerationSettings settings, CancellationToken cancellationToken = default);
}
