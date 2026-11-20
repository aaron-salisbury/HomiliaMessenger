using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Integrations.LocalModels;
using System.Collections.Generic;
using System.Linq;

namespace HomiliaMessenger.Integrations.ImageGeneration;

/// <summary>
/// What a model's shared file does.
/// </summary>
internal enum ImageComponentRole
{
    /// <summary>
    /// A language model that reads prompts for a diffusion model, such as Qwen3-4B for FLUX.2 [klein] 4B.
    /// </summary>
    TextEncoder,

    /// <summary>
    /// The VAE that turns a diffusion model's drawing into pixels.
    /// </summary>
    Vae
}

/// <summary>
/// A file a model is drawn with that is published apart from it and shared by every model that needs it, such as the text
/// encoder and VAE of FLUX.2 [klein] 4B and its fine-tunes. Downloaded once, into a folder of its own, and removed with
/// the last downloaded model that uses it.
/// </summary>
/// <param name="BaseUrl">Where the file is downloaded from, pinned to a specific revision so its hash stays valid.</param>
internal sealed record ImageModelComponent(string Id, string DisplayName, ImageComponentRole Role, string License, string BaseUrl, LocalModelFile File);

/// <summary>
/// An image model the application knows how to download and run with stable-diffusion.cpp. Image models are a category
/// of their own, apart from conversational and embedding models, even when they share the GGUF format.
/// </summary>
/// <param name="BaseUrl">Where the files are downloaded from, pinned to a specific revision so the hashes stay valid.</param>
/// <param name="Files">
/// The model's own files: first the file the runtime opens (a self-contained checkpoint, or for FLUX.2 [klein] the
/// diffusion model alone), then any notices that come with it, such as its license.
/// </param>
/// <param name="Settings">How this model draws; built-in models use settings verified with the integrated runtime.</param>
/// <param name="IsVerified">Whether this exact file and these settings were tested with the integrated runtime.</param>
/// <param name="Repository">For a model the user added, the Hugging Face repository it came from.</param>
/// <param name="Purposes">What the model is offered for; models added before selfies existed make contact photos.</param>
/// <param name="SelfieSettings">How the model draws selfies, when it is offered for them; otherwise its <see cref="Settings"/>.</param>
/// <param name="SelfieDescription">How the model is described among selfie models; otherwise its <see cref="Description"/>.</param>
/// <param name="Processor">What it runs on; models added before graphics cards were supported run on the processor.</param>
/// <param name="Family">Its architecture, which decides how it is opened, how selfies keep a face, and which LoRAs fit it.</param>
/// <param name="PromptStyle">How its prompts are written, when chosen for it; otherwise as its family reads them.</param>
/// <param name="Components">The shared files it is drawn with besides its own, such as a text encoder and VAE.</param>
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
    string? SelfieDescription = null,
    ImageProcessor Processor = ImageProcessor.Cpu,
    ImageModelFamily Family = ImageModelFamily.StableDiffusion1,
    ImagePromptStyle? PromptStyle = null,
    IReadOnlyList<ImageModelComponent>? Components = null)
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

    /// <summary>
    /// The shared files it needs, none for a self-contained checkpoint.
    /// </summary>
    public IReadOnlyList<ImageModelComponent> SharedFiles => Components ?? [];

    /// <summary>
    /// What downloading the model takes, with its shared files.
    /// </summary>
    public long DownloadBytes => Files.Sum(file => file.Size) + SharedFiles.Sum(component => component.File.Size);

    public bool IsUserAdded => Repository is not null;

    /// <summary>
    /// Whether <see cref="Checkpoint"/> holds the diffusion model alone, opened with its shared text encoder and VAE.
    /// </summary>
    public bool IsDiffusionModelOnly => Family == ImageModelFamily.Flux2Klein4B;

    public ImagePromptStyle Style => PromptStyle ?? IImageModelLibrary.PromptStyleOf(Family);

    public SelfieFaceSource FaceSource => Family == ImageModelFamily.Flux2Klein4B ? SelfieFaceSource.ReferenceImage : SelfieFaceSource.FaceMatching;
}

/// <summary>
/// The image models offered for download: DreamShaper 8 LCM on the processor, and, on a graphics card, FLUX.2 [klein] 4B
/// and DreamShaper XL Lightning, as chosen in the GPU image experiment.
/// </summary>
internal static class ImageModelCatalog
{
    internal const string DREAMSHAPER_8_LCM = "dreamshaper-8-lcm-q8_0";
    internal const string FLUX2_KLEIN_4B = "flux-2-klein-4b-q4_0";
    internal const string DREAMSHAPER_XL_LIGHTNING = "dreamshaper-xl-lightning";

    /// <summary>
    /// CreativeML Open RAIL-M, the license of Stable Diffusion 1.x and its fine-tunes, as published with the model.
    /// </summary>
    internal const string OPENRAIL_M = "CreativeML Open RAIL-M";

    /// <summary>
    /// CreativeML Open RAIL++-M, the license of SDXL and its fine-tunes, with the same use restrictions as Open RAIL-M.
    /// </summary>
    internal const string OPENRAIL_PLUS_PLUS_M = "CreativeML Open RAIL++-M";

    internal const string APACHE_2 = "Apache 2.0";

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

    /// <summary>
    /// FLUX.2 [klein] 4B as measured in the GPU image experiment: the distilled model's 4 steps with Euler and CFG 1 (so no
    /// negative prompt), at 1024×1024 for contact photos and 896×1152 for selfies.
    /// </summary>
    internal static readonly ImageGenerationSettings KLEIN_SETTINGS = new(ImageGenerationSettings.LARGE_PORTRAIT_SIZE, ImageGenerationSettings.LARGE_PORTRAIT_SIZE, "euler", null, 4, 1f);
    internal static readonly ImageGenerationSettings KLEIN_SELFIE_SETTINGS = new(ImageGenerationSettings.LARGE_SELFIE_WIDTH, ImageGenerationSettings.LARGE_SELFIE_HEIGHT, "euler", null, 4, 1f);

    /// <summary>
    /// DreamShaper XL Lightning as its model card recommends and the GPU image experiment confirmed: Euler ancestral with
    /// the SGM uniform schedule (DPM++ SDE Karras left speckles with this runtime), 6 steps, CFG 2.
    /// </summary>
    internal static readonly ImageGenerationSettings DREAMSHAPER_XL_SETTINGS = new(ImageGenerationSettings.LARGE_PORTRAIT_SIZE, ImageGenerationSettings.LARGE_PORTRAIT_SIZE, "euler_a", "sgm_uniform", 6, 2f);
    internal static readonly ImageGenerationSettings DREAMSHAPER_XL_SELFIE_SETTINGS = new(ImageGenerationSettings.LARGE_SELFIE_WIDTH, ImageGenerationSettings.LARGE_SELFIE_HEIGHT, "euler_a", "sgm_uniform", 6, 2f);

    /// <summary>
    /// Qwen3-4B at Q4_K_M, the text encoder FLUX.2 [klein] 4B reads prompts with; in the experiment it drew practically
    /// the same images as Q8_0 at about half the size. Unsloth's GGUF conversion of Qwen's Apache 2.0 model.
    /// </summary>
    internal static readonly ImageModelComponent QWEN3_4B = new(
        "qwen3-4b-q4_k_m",
        "Qwen3-4B text encoder",
        ImageComponentRole.TextEncoder,
        APACHE_2,
        "https://huggingface.co/unsloth/Qwen3-4B-GGUF/resolve/22c9fc8a8c7700b76a1789366280a6a5a1ad1120/",
        new("Qwen3-4B-Q4_K_M.gguf", 2497281312, "f6f851777709861056efcdad3af01da38b31223a3ba26e61a4f8bf3a2195813a"));

    /// <summary>
    /// The FLUX.2 VAE, as published with FLUX.2 [klein] 4B (Apache 2.0), from Comfy-Org's ungated copy of its files.
    /// </summary>
    internal static readonly ImageModelComponent FLUX2_VAE = new(
        "flux2-vae",
        "FLUX.2 VAE",
        ImageComponentRole.Vae,
        APACHE_2,
        "https://huggingface.co/Comfy-Org/Flux-2-Klein-4B/resolve/5b3abca5c0dc1ecd4eaf3340868d09063fc1c965/",
        new("split_files/vae/flux2-vae.safetensors", 336211292, "868fe7b343cc8f3a19dbcfcafbc3d5f888802be3f89bd81b65b3621a066ce8f3"));

    /// <summary>
    /// What every FLUX.2 [klein] 4B model is drawn with besides its diffusion model.
    /// </summary>
    internal static IReadOnlyList<ImageModelComponent> KleinComponents { get; } = [QWEN3_4B, FLUX2_VAE];

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
                + "2.6 GB to download, made 0.8 GB once prepared."),
        new(
            FLUX2_KLEIN_4B,
            "FLUX.2 [klein] 4B",
            "Draws photorealistic contact photos that follow long, specific descriptions closely, on a graphics card: about 5 seconds each on an RTX 4070 Ti with the CUDA runtime. "
                + "Black Forest Labs' model, read by the Qwen3-4B language model rather than CLIP, so describe the person in plain sentences. "
                + "Uses about 8 GB of graphics memory, and works on smaller cards by keeping part of the model in the computer's memory, more slowly. "
                + "5.3 GB to download, of which the text encoder and VAE (2.8 GB) are shared with any other FLUX.2 [klein] 4B model.",
            APACHE_2,
            "https://huggingface.co/black-forest-labs/FLUX.2-klein-4B",
            // leejet/FLUX.2-klein-4B-GGUF: the distilled FLUX.2 [klein] 4B converted with stable-diffusion.cpp, Q4_0, which
            // in the experiment drew practically the same images as Q8_0.
            "https://huggingface.co/leejet/FLUX.2-klein-4B-GGUF/resolve/3b1f5a9dc3abb32238b053aeb3d823c30afdacbd/",
            [new("flux-2-klein-4b-Q4_0.gguf", 2460378560, "d1023499ef3f2f82ff7c50e6778495195c1b6cc34835741778868428111f9ff4")],
            KLEIN_SETTINGS,
            true,
            Purposes: ImageModelPurpose.Portraits | ImageModelPurpose.Selfies,
            SelfieSettings: KLEIN_SELFIE_SETTINGS,
            SelfieDescription: "Takes selfies that keep the contact's face by drawing from their profile photo itself, and shows what the conversation describes, "
                + "such as being splashed after jumping in a puddle, a dog licking a cheek, or a sign with words on it: about 8 seconds each on an RTX 4070 Ti with the CUDA runtime, "
                + "and about 13 seconds with the Vulkan backend included with the app. The same download as contact photos; no face matching is needed.",
            Processor: ImageProcessor.Gpu,
            Family: ImageModelFamily.Flux2Klein4B,
            Components: KleinComponents),
        new(
            DREAMSHAPER_XL_LIGHTNING,
            "DreamShaper XL Lightning",
            "Draws polished, stylized photographic contact photos on a graphics card: about 6 seconds each on an RTX 4070 Ti. "
                + "An SDXL model, so the many LoRAs made for SDXL 1.0 fit it. Follows long or unusual descriptions less closely than FLUX.2 [klein]. "
                + "Uses about 8 GB of graphics memory; 6.9 GB to download.",
            OPENRAIL_PLUS_PLUS_M,
            "https://huggingface.co/Lykon/dreamshaper-xl-lightning",
            // Lykon/dreamshaper-xl-lightning: the published checkpoint, at half precision with its CLIP text encoders and
            // VAE, which the runtime opens as it is.
            "https://huggingface.co/Lykon/dreamshaper-xl-lightning/resolve/561086c22aba3925b1e8803a73857f061635c7c3/",
            [new("DreamShaperXL_Lightning.safetensors", 6939220250, "fdbe56354b8f876b736f24d3ad867ecd4140c019f57642ec8ddd878088d44f64")],
            DREAMSHAPER_XL_SETTINGS,
            true,
            Purposes: ImageModelPurpose.Portraits | ImageModelPurpose.Selfies,
            SelfieSettings: DREAMSHAPER_XL_SELFIE_SETTINGS,
            SelfieDescription: "Takes selfies that keep the contact's face with face matching for SDXL: about 8 seconds each on an RTX 4070 Ti. "
                + "Face matching tends to copy the pose of the profile photo, and busy scenes come out less as described than with FLUX.2 [klein]; its strength is the SDXL LoRAs that fit it. "
                + "Choosing it for selfies also downloads IP-Adapter Plus-Face for SDXL (0.8 GB, Apache 2.0) and the image encoder DreamShaper 8's face matching uses, if needed.",
            Processor: ImageProcessor.Gpu,
            Family: ImageModelFamily.StableDiffusionXL)
    ];
}

/// <summary>
/// Face matching for selfies, as tested in the local selfie experiment: IP-Adapter Plus-Face for Stable Diffusion 1.5,
/// and the ViT-H/14 CLIP image encoder it reads a photo's face with, both from h94/IP-Adapter at a pinned revision. The
/// encoder is published at full precision (2.5 GB); it is converted to Q8_0 with the integrated runtime after
/// downloading, which in the experiment drew indistinguishable selfies while lowering peak memory from 6.2 GB to 4.5 GB.
/// SDXL models use IP-Adapter Plus-Face for SDXL from the same repository, which reads faces with the same encoder.
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

    /// <summary>
    /// How strongly an SDXL selfie keeps the face, as the GPU image experiment drew it.
    /// </summary>
    internal const float SDXL_STRENGTH = 0.5f;

    internal static readonly LocalModelFile ADAPTER = new("models/ip-adapter-plus-face_sd15.safetensors", 98183288, "1c9edc21af6f737dc1d6e0e734190e976cfacf802d6b024b77aa3be922f7569b");

    internal static readonly LocalModelFile SDXL_ADAPTER = new("sdxl_models/ip-adapter-plus-face_sdxl_vit-h.safetensors", 847517512, "677ad8860204f7d0bfba12d29e6c31ded9beefdf3e4bbd102518357d31a292c1");

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

    internal static FaceMatchingDefinition Definition { get; } = new(BASE_URL, ADAPTER, IMAGE_ENCODER, PREPARED_ENCODER_BYTES, SDXL_ADAPTER);

    /// <summary>
    /// How strongly a selfie of a model of <paramref name="family"/> keeps the face.
    /// </summary>
    internal static float StrengthFor(ImageModelFamily family)
    {
        return family == ImageModelFamily.StableDiffusionXL ? SDXL_STRENGTH : STRENGTH;
    }
}

/// <summary>
/// Where face matching's files come from, and the size of its prepared encoder; <see cref="FaceMatchingCatalog.Definition"/>
/// in the app, and smaller stand-ins in tests.
/// </summary>
/// <param name="Adapter">The adapter for Stable Diffusion 1.x models.</param>
/// <param name="SdxlAdapter">The adapter for SDXL models, which read faces with the same encoder.</param>
internal sealed record FaceMatchingDefinition(string BaseUrl, LocalModelFile Adapter, LocalModelFile ImageEncoder, long PreparedEncoderBytes, LocalModelFile? SdxlAdapter = null)
{
    public IReadOnlyList<LocalModelFile> Downloads => [Adapter, ImageEncoder];

    public long DownloadBytes => Adapter.Size + ImageEncoder.Size;

    public long DiskBytes => Adapter.Size + PreparedEncoderBytes;

    /// <summary>
    /// The adapter selfies of a model of <paramref name="family"/> keep a face with, or <see langword="null"/> when there
    /// is none for it.
    /// </summary>
    public LocalModelFile? AdapterFor(ImageModelFamily family)
    {
        return family switch
        {
            ImageModelFamily.StableDiffusion1 => Adapter,
            ImageModelFamily.StableDiffusionXL => SdxlAdapter,
            _ => null
        };
    }

    /// <summary>
    /// Every adapter, for keeping what is downloaded while another is.
    /// </summary>
    public IEnumerable<LocalModelFile> Adapters => SdxlAdapter is null ? [Adapter] : [Adapter, SdxlAdapter];
}
