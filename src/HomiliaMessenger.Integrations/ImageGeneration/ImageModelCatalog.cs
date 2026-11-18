using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Integrations.LocalModels;
using System.Collections.Generic;
using System.Linq;

namespace HomiliaMessenger.Integrations.ImageGeneration;

/// <summary>
/// An image model the application knows how to download and run with stable-diffusion.cpp. Image models are a category
/// of their own, apart from conversational and embedding models, even when they share the GGUF format.
/// </summary>
/// <param name="BaseUrl">Where the files are downloaded from, pinned to a specific revision so the hashes stay valid.</param>
/// <param name="Files">
/// The model's files: first the self-contained GGUF checkpoint the runtime opens (with its text encoder and VAE), then
/// any notices that come with it, such as its license.
/// </param>
/// <param name="Settings">How this model draws; built-in models use settings verified with the integrated runtime.</param>
/// <param name="IsVerified">Whether this exact file and these settings were tested with the integrated runtime.</param>
/// <param name="Repository">For a model the user added, the Hugging Face repository it came from.</param>
/// <param name="Purposes">What the model is offered for; models added before selfies existed make contact photos.</param>
/// <param name="SelfieSettings">How the model draws selfies, when it is offered for them; otherwise its <see cref="Settings"/>.</param>
/// <param name="SelfieDescription">How the model is described among selfie models; otherwise its <see cref="Description"/>.</param>
internal sealed record ImageModelDefinition(
    string Id,
    string DisplayName,
    string Description,
    string License,
    string? LicenseUrl,
    string BaseUrl,
    IReadOnlyList<LocalModelFile> Files,
    ImageGenerationSettings Settings,
    bool IsVerified,
    string? Repository = null,
    ImageModelPurpose Purposes = ImageModelPurpose.Portraits,
    ImageGenerationSettings? SelfieSettings = null,
    string? SelfieDescription = null)
{
    public bool IsOffered(ImageModelPurpose purpose)
    {
        return purpose != ImageModelPurpose.None && (Purposes & purpose) == purpose;
    }

    /// <summary>
    /// How the model draws for <paramref name="purpose"/>.
    /// </summary>
    public ImageGenerationSettings SettingsFor(ImageModelPurpose purpose)
    {
        return purpose == ImageModelPurpose.Selfies ? SelfieSettings ?? Settings : Settings;
    }

    public LocalModelFile Checkpoint => Files[0];

    public long DownloadBytes => Files.Sum(file => file.Size);

    public bool IsUserAdded => Repository is not null;
}

/// <summary>
/// The image models offered for download.
/// </summary>
internal static class ImageModelCatalog
{
    internal const string DREAMSHAPER_8_LCM = "dreamshaper-8-lcm-q8_0";

    /// <summary>
    /// CreativeML Open RAIL-M, the license of Stable Diffusion 1.x and its fine-tunes, as published with the model.
    /// </summary>
    internal const string OPENRAIL_M = "CreativeML Open RAIL-M";

    /// <summary>
    /// DreamShaper 8 LCM as tested in the local portrait experiment: 512×512, the LCM sampler with the runtime's default
    /// scheduler for it, 4 steps, and CFG 1, decoded with the regular VAE embedded in the checkpoint (not TAESD).
    /// </summary>
    internal static readonly ImageGenerationSettings DREAMSHAPER_SETTINGS = new(512, 512, "lcm", null, 4, 1f);

    /// <summary>
    /// DreamShaper 8 LCM for selfies, as chosen in the local selfie experiment: a 512×640 portrait canvas, which drew far
    /// fewer duplicated faces than 512×768, and CFG 1.5, at which a negative prompt and emphasis weights take effect with
    /// less oversaturation than CFG 2 at the same cost.
    /// </summary>
    internal static readonly ImageGenerationSettings DREAMSHAPER_SELFIE_SETTINGS = new(ImageGenerationSettings.SELFIE_WIDTH, ImageGenerationSettings.SELFIE_HEIGHT, "lcm", null, 4, 1.5f);

    internal static IReadOnlyList<ImageModelDefinition> Models { get; } =
    [
        new(
            DREAMSHAPER_8_LCM,
            "DreamShaper 8 LCM",
            "Generates polished, slightly stylized photographic portraits locally. Runs on the CPU; a photo takes about 20 seconds on a fast desktop and longer on a laptop, using about 4 GB of memory while it works.",
            OPENRAIL_M,
            "https://huggingface.co/NishQ1/vora-create-sd15/blob/990b82c686c3c047df2f1926460b4915773d64c5/LICENSE.txt",
            // NishQ1/vora-create-sd15: Lykon's DreamShaper 8 LCM (a Stable Diffusion 1.5 fine-tune) converted with
            // stable-diffusion.cpp, linear weights in Q8_0, with its CLIP text encoder and VAE in the same file.
            "https://huggingface.co/NishQ1/vora-create-sd15/resolve/990b82c686c3c047df2f1926460b4915773d64c5/",
            [
                new("vora-create-dreamshaper8-lcm-q8_0.gguf", 1802206336, "dd06826d1eca711efa38190395a5ee949d0427fc6422c39433358a815e5b066c"),
                new("LICENSE.txt", 14385, "be351ebe7ac01bcdbb018639aadcfd38f136b7dc3f2a3d4d3a24db51d1b210ef")
            ],
            DREAMSHAPER_SETTINGS,
            true,
            Purposes: ImageModelPurpose.Portraits | ImageModelPurpose.Selfies,
            SelfieSettings: DREAMSHAPER_SELFIE_SETTINGS,
            SelfieDescription: "Takes selfies that keep the contact's face from their profile photo, on the CPU: about 35 seconds each on a fast desktop, using about 4.5 GB of memory. "
                + "The same 1.8 GB model as contact photos, downloaded once for both. Choosing it for selfies also downloads face matching (IP-Adapter Plus-Face and an image encoder, Apache 2.0): "
                + "2.6 GB to download, made 0.8 GB once prepared.")
    ];
}

/// <summary>
/// Face matching for selfies, as tested in the local selfie experiment: IP-Adapter Plus-Face for Stable Diffusion 1.5,
/// and the ViT-H/14 CLIP image encoder it reads a photo's face with, both from h94/IP-Adapter at a pinned revision. The
/// encoder is published at full precision (2.5 GB); it is converted to Q8_0 with the integrated runtime after
/// downloading, which in the experiment drew indistinguishable selfies while lowering peak memory from 6.2 GB to 4.5 GB.
/// </summary>
/// <remarks>
/// Source: <see href="https://huggingface.co/h94/IP-Adapter">h94/IP-Adapter</see> (Tencent AI Lab's IP-Adapter, Apache 2.0).
/// </remarks>
internal static class FaceMatchingCatalog
{
    internal const string DIRECTORY_NAME = "face-matching";
    internal const string NAME = "IP-Adapter Plus-Face";
    internal const string LICENSE = "Apache 2.0";
    internal const string LICENSE_URL = "https://huggingface.co/h94/IP-Adapter/blob/018e402774aeeddd60609b4ecdb7e298259dc729/README.md";
    internal const string BASE_URL = "https://huggingface.co/h94/IP-Adapter/resolve/018e402774aeeddd60609b4ecdb7e298259dc729/";

    /// <summary>
    /// How strongly a selfie keeps the face: in the experiment, 0.25 to 0.4 kept the likeness equally well, and lower
    /// strengths let slightly less of the photo's clothing through.
    /// </summary>
    internal const float STRENGTH = 0.3f;

    internal static readonly LocalModelFile ADAPTER = new("models/ip-adapter-plus-face_sd15.safetensors", 98183288, "1c9edc21af6f737dc1d6e0e734190e976cfacf802d6b024b77aa3be922f7569b");

    internal static readonly LocalModelFile IMAGE_ENCODER = new("models/image_encoder/model.safetensors", 2528373448, "6ca9667da1ca9e0b0f75e46bb030f7e011f44f86cbfb8d5a36590fcd7507b030");

    /// <summary>
    /// The encoder converted to Q8_0, kept in place of the downloaded one.
    /// </summary>
    internal const string PREPARED_ENCODER = "clip-vision-h-q8_0.gguf";

    /// <summary>
    /// The converted encoder's size, and its SHA-256 as the experiment's <c>sd-cli</c> made it with the same runtime.
    /// </summary>
    internal const long PREPARED_ENCODER_BYTES = 676139648;
    internal const string PREPARED_ENCODER_SHA256 = "37f5456cb0a1e64e277c23ae483a239606f25c017dd3f856f679643936460fbc";

    internal static FaceMatchingDefinition Definition { get; } = new(BASE_URL, ADAPTER, IMAGE_ENCODER, PREPARED_ENCODER_BYTES);
}

/// <summary>
/// Where face matching's files come from, and the size of its prepared encoder; <see cref="FaceMatchingCatalog.Definition"/>
/// in the app, and smaller stand-ins in tests.
/// </summary>
internal sealed record FaceMatchingDefinition(string BaseUrl, LocalModelFile Adapter, LocalModelFile ImageEncoder, long PreparedEncoderBytes)
{
    public IReadOnlyList<LocalModelFile> Downloads => [Adapter, ImageEncoder];

    public long DownloadBytes => Adapter.Size + ImageEncoder.Size;

    public long DiskBytes => Adapter.Size + PreparedEncoderBytes;
}

