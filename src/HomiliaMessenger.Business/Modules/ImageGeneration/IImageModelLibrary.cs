using HomiliaMessenger.Business.Modules.Agents;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.ImageGeneration;

/// <summary>
/// What an image model is used for. Each use chooses its own model; one model may serve both.
/// </summary>
[Flags]
public enum ImageModelPurpose
{
    None = 0,

    /// <summary>
    /// Contact photos, generated from a description on the Contacts page.
    /// </summary>
    Portraits = 1,

    /// <summary>
    /// Selfies contacts send in chats, which also need face matching (see <see cref="FaceMatching"/>).
    /// </summary>
    Selfies = 2
}

/// <summary>
/// What an image model runs on. Settings lists the two apart: processor models work on any computer, graphics card models
/// need a supported GPU and are much faster on one.
/// </summary>
public enum ImageProcessor
{
    /// <summary>
    /// The computer's processor, as DreamShaper 8 LCM runs: no graphics card needed.
    /// </summary>
    Cpu,

    /// <summary>
    /// A graphics card, through the runtime's CUDA (NVIDIA) or Vulkan backend.
    /// </summary>
    Gpu
}

/// <summary>
/// How a model reads its prompt, which decides how prompts are written for it.
/// </summary>
public enum ImagePromptStyle
{
    /// <summary>
    /// Read by a CLIP text encoder (Stable Diffusion 1.x and SDXL): short phrases, with emphasis weights such as
    /// <c>(soaking wet:1.5)</c> that the runtime applies, and a negative prompt when guidance is above 1.
    /// </summary>
    Weighted,

    /// <summary>
    /// Read by a language model (FLUX.2 [klein]'s Qwen3): plain descriptive sentences. Weights are not understood and are
    /// read as text, so none are written.
    /// </summary>
    Plain
}

/// <summary>
/// How a selfie keeps the contact's face.
/// </summary>
public enum SelfieFaceSource
{
    /// <summary>
    /// IP-Adapter Plus-Face and its image encoder (see <see cref="FaceMatching"/>), downloaded besides the model, as for
    /// Stable Diffusion 1.x and SDXL.
    /// </summary>
    FaceMatching,

    /// <summary>
    /// The model takes the contact's photo itself as a reference image, as FLUX.2 [klein] does; nothing else is needed.
    /// </summary>
    ReferenceImage
}

/// <summary>
/// An image model that can run on this computer, kept apart from conversational and embedding models, as listed for one
/// purpose.
/// </summary>
/// <param name="LicenseUrl">Where the model's license can be read, when known.</param>
/// <param name="DownloadBytes">The size of everything the purpose needs: for selfies, the model and face matching.</param>
/// <param name="IsDownloaded">Whether everything the purpose needs is downloaded.</param>
/// <param name="IsSelected">Whether images for this purpose are generated with it, once it is downloaded.</param>
/// <param name="IsUserAdded">Whether the user added it from Hugging Face; removing it forgets it too.</param>
/// <param name="IsVerified">
/// Whether the model and its settings were tested with this app's runtime for this purpose. A model added from Hugging
/// Face is not.
/// </param>
/// <param name="Purposes">Everything the model is offered for, which may be more than the purpose it is listed for.</param>
/// <param name="Processor">What the model runs on.</param>
/// <param name="Family">The model's architecture, which decides the LoRAs that fit it.</param>
/// <param name="PromptStyle">How prompts are written for it.</param>
/// <param name="FaceSource">How its selfies keep a face.</param>
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
    ImageGenerationSettings Settings,
    ImageModelPurpose Purposes = ImageModelPurpose.Portraits,
    ImageProcessor Processor = ImageProcessor.Cpu,
    ImageModelFamily Family = ImageModelFamily.StableDiffusion1,
    ImagePromptStyle PromptStyle = ImagePromptStyle.Weighted,
    SelfieFaceSource FaceSource = SelfieFaceSource.FaceMatching);

/// <summary>
/// What selfies need besides an image model: IP-Adapter Plus-Face, which carries a contact's face from their photo into
/// the selfie, and the image encoder that reads the photo for it. Shared by every selfie model.
/// </summary>
/// <param name="DownloadBytes">What is downloaded.</param>
/// <param name="DiskBytes">What is kept once prepared: the encoder is made smaller after downloading.</param>
public sealed record FaceMatching(string Name, string License, string? LicenseUrl, long DownloadBytes, long DiskBytes, bool IsDownloaded);

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
    /// Stable Diffusion 1.x and its fine-tunes, such as DreamShaper 8, on the processor or a graphics card.
    /// </summary>
    StableDiffusion1,
    StableDiffusion2,

    /// <summary>
    /// SDXL and its fine-tunes, such as DreamShaper XL Lightning, on a graphics card.
    /// </summary>
    StableDiffusionXL,

    /// <summary>
    /// FLUX.2 [klein] 4B and its fine-tunes: a diffusion model alone, read with the Qwen3-4B text encoder and decoded with
    /// the FLUX.2 VAE, which are separate files shared by every such model. On a graphics card.
    /// </summary>
    Flux2Klein4B,

    /// <summary>
    /// Another image model, such as SD3, FLUX.1, or a larger FLUX.2.
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
/// <param name="Processor">What the files were checked for: a model that runs on a graphics card may be refused for the processor.</param>
public sealed record ImageModelLookup(HuggingFaceLookupStatus Status, string RepoId, string? Revision, string? License, string? PipelineTag, IReadOnlyList<ImageModelFile> Files, string? Problem, ImageProcessor Processor = ImageProcessor.Cpu)
{
    public string Url => "https://huggingface.co/" + RepoId;

    public static ImageModelLookup Unsuccessful(HuggingFaceLookupStatus status, string repoId, string problem, ImageProcessor processor = ImageProcessor.Cpu)
    {
        return new(status, repoId, null, null, null, [], problem, processor);
    }
}

/// <summary>
/// Capability to manage the image models that run on this computer and choose the one photos are generated with.
/// </summary>
public interface IImageModelLibrary
{
    /// <summary>
    /// The models offered for <paramref name="purpose"/>, whether each is downloaded, and which is in use for it.
    /// </summary>
    IReadOnlyList<ImageModel> List(ImageModelPurpose purpose = ImageModelPurpose.Portraits);

    /// <summary>
    /// Face matching, which selfies need with any model.
    /// </summary>
    FaceMatching GetFaceMatching();

    /// <summary>
    /// Downloads and verifies a model and, for selfies, face matching, skipping what is already downloaded. When no model
    /// is in use for the purpose yet, the downloaded one is used.
    /// </summary>
    Task<LocalModelDownloadResult> DownloadAsync(string modelId, ImageModelPurpose purpose = ImageModelPurpose.Portraits, IProgress<LocalModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes what <paramref name="purpose"/> alone needs of a model, freeing its disk space. For contact photos, or for
    /// a model offered only for selfies, that is the model itself (one the user added is forgotten too); for a model that
    /// also makes contact photos, removing it from selfies removes face matching instead and keeps the model. When the
    /// removed model was in use and another is downloaded, that one is used instead.
    /// </summary>
    Task<bool> DeleteAsync(string modelId, ImageModelPurpose purpose = ImageModelPurpose.Portraits, CancellationToken cancellationToken = default);

    /// <summary>
    /// Chooses the model images for <paramref name="purpose"/> are generated with. Returns <see langword="false"/> for an
    /// unknown model, or one not offered for the purpose.
    /// </summary>
    bool Select(string modelId, ImageModelPurpose purpose = ImageModelPurpose.Portraits);

    /// <summary>
    /// Lists the GGUF files in a Hugging Face repository, given as <c>owner/name</c> or its address, and reads each one's
    /// header to tell whether this app can run it on <paramref name="processor"/>: Stable Diffusion 1.x on either, and
    /// SDXL and FLUX.2 [klein] 4B on a graphics card only. Nothing is downloaded beyond the headers.
    /// </summary>
    Task<ImageModelLookup> LookUpAsync(string repository, ImageProcessor processor = ImageProcessor.Cpu, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a supported file from a lookup to the models offered for <paramref name="purpose"/>, on the processor it was
    /// looked up for, with the settings the user gave for it, ready to download. Its compatibility is not verified. A file
    /// already added for the other purpose is offered for this one too.
    /// </summary>
    /// <param name="promptStyle">How its prompts are written; when <see langword="null"/>, as its family is read (see <see cref="PromptStyleOf"/>).</param>
    Task<LocalModelAddResult> AddAsync(ImageModelLookup lookup, ImageModelFile file, ImageGenerationSettings settings, ImageModelPurpose purpose = ImageModelPurpose.Portraits, ImagePromptStyle? promptStyle = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// How a model of <paramref name="family"/> reads prompts, from its text encoder: CLIP for Stable Diffusion, a language
    /// model for FLUX.2.
    /// </summary>
    static ImagePromptStyle PromptStyleOf(ImageModelFamily family)
    {
        return family == ImageModelFamily.Flux2Klein4B ? ImagePromptStyle.Plain : ImagePromptStyle.Weighted;
    }
}
