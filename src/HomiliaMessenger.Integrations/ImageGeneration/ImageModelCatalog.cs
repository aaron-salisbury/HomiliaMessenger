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
    string? Repository = null)
{
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
            true)
    ];
}
