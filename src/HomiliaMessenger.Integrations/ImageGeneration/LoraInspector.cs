using HomiliaMessenger.Business.Modules.ImageGeneration;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HomiliaMessenger.Integrations.ImageGeneration;

/// <summary>
/// What a LoRA file shows about itself: the base model it fits, and the trigger words and strength it suggests.
/// </summary>
internal sealed record LoraInspection(ImageModelFamily Base, string BaseDescription, string? Note, string? TriggerWords, string? TriggerWordsSource, float? Strength, string? Problem = null);

/// <summary>
/// Reads a LoRA's <c>.safetensors</c> header (the names and shapes of its tensors and its training metadata, never its
/// weights) and the notes other tools keep beside it, to tell which base model it was trained for and how to call it up.
/// </summary>
/// <remarks>
/// <para>
/// The tensors decide the base wherever they can, since they are what must fit: a Stable Diffusion LoRA's cross-attention
/// key projection reads the text encoder's features, 768 wide for 1.x, 1024 for 2.x and 2048 for SDXL, and SDXL LoRAs
/// that train the text encoders name two of them. FLUX LoRAs train double- and single-stream blocks; FLUX.1 has 19 and 38
/// of them, with modulation in each, where FLUX.2 [klein] 4B has 5 and 20, with modulation shared, both 3072 wide. Training
/// metadata (kohya's <c>ss_base_model_version</c>, the model spec's <c>modelspec.architecture</c>) and Civitai's
/// <c>baseModel</c> tell apart what tensors cannot, such as a FLUX.1 LoRA trained on only a few blocks.
/// </para>
/// <para>
/// Trigger words come, in order of trust, from Civitai's <c>trainedWords</c> (in the <c>.civitai.info</c> file Stability
/// Matrix and the Civitai Helper extension save), the Stable Diffusion web UI's <c>activation text</c> (in its
/// <c>.json</c> file, with its <c>preferred weight</c>), the model spec's <c>modelspec.trigger_phrase</c>, and last the
/// training captions' tags (kohya's <c>ss_tag_frequency</c>): those in nearly every caption, which is how a trigger word is
/// trained, leaving out generic tags such as "solo".
/// </para>
/// </remarks>
internal static partial class LoraInspector
{
    /// <summary>
    /// The longest header read; LoRA headers are usually well under a megabyte, with long caption statistics a few.
    /// </summary>
    internal const int MAX_HEADER_BYTES = 64 * 1024 * 1024;

    private const long SD1_CONTEXT = 768;
    private const long SD2_CONTEXT = 1024;
    private const long SDXL_CONTEXT = 2048;
    private const long FLUX_WIDTH = 3072;
    private const int KLEIN_4B_DOUBLE_BLOCKS = 5;
    private const int KLEIN_4B_SINGLE_BLOCKS = 20;

    private static readonly HashSet<string> GENERIC_TAGS = new(StringComparer.OrdinalIgnoreCase)
    {
        "1girl", "1boy", "2girls", "solo", "looking at viewer", "simple background", "white background", "smile", "realistic", "photorealistic",
        "portrait", "upper body", "photo", "a photo of", "woman", "man", "person", "best quality", "masterpiece", "high quality", "outdoors",
        "indoors", "standing", "closed mouth", "open mouth", "blurry", "blurry background", "lips", "teeth", "short hair", "long hair"
    };

    private static readonly string[] RETRAINED_SDXL = ["pony", "illustrious", "noob", "animagine"];

    /// <summary>
    /// Inspects the LoRA at <paramref name="path"/> and the notes beside it.
    /// </summary>
    internal static LoraInspection Inspect(string path)
    {
        Dictionary<string, long[]> tensors;
        Dictionary<string, string> metadata;

        try
        {
            (tensors, metadata) = ReadHeader(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        {
            return new LoraInspection(ImageModelFamily.Unknown, "Not readable", null, null, null, null, "The file's header could not be read: " + error.Message);
        }

        // Files from the user's folder may hold anything; one that cannot be made sense of is listed as unreadable, never
        // stopping the scan of the others, or a drawing that uses them.
        try
        {
            Sidecar sidecar = ReadSidecars(path);
            (ImageModelFamily family, string description) = Base(tensors, metadata, sidecar.BaseModel);
            string? note = Note(family, metadata, sidecar.BaseModel);
            (string? words, string? source) = TriggerWords(metadata, sidecar);

            return new LoraInspection(family, description, note, words, source, sidecar.Strength);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException or ArgumentException or JsonException)
        {
            return new LoraInspection(ImageModelFamily.Unknown, "Not readable", null, null, null, null, "The file's notes could not be read: " + error.Message);
        }
    }

    /// <summary>
    /// Reads the tensors' names and shapes and the metadata from a <c>.safetensors</c> header.
    /// </summary>
    internal static (Dictionary<string, long[]> Tensors, Dictionary<string, string> Metadata) ReadHeader(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> prefix = stackalloc byte[8];
        stream.ReadExactly(prefix);
        ulong length = BinaryPrimitives.ReadUInt64LittleEndian(prefix);

        if (length is 0 or > MAX_HEADER_BYTES || (long)length > stream.Length - 8)
        {
            throw new InvalidDataException("this is not a safetensors file.");
        }

        byte[] header = new byte[length];
        stream.ReadExactly(header);

        return ParseHeader(header);
    }

    internal static (Dictionary<string, long[]> Tensors, Dictionary<string, string> Metadata) ParseHeader(ReadOnlySpan<byte> header)
    {
        Dictionary<string, long[]> tensors = new(StringComparer.Ordinal);
        Dictionary<string, string> metadata = new(StringComparer.Ordinal);
        Utf8JsonReader reader = new(header);
        using JsonDocument document = JsonDocument.ParseValue(ref reader);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("the header is not a JSON object.");
        }

        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Name == "__metadata__" && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty entry in property.Value.EnumerateObject())
                {
                    metadata[entry.Name] = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString()! : entry.Value.GetRawText();
                }
            }
            else if (property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("shape", out JsonElement shape) && shape.ValueKind == JsonValueKind.Array)
            {
                tensors[property.Name] = [.. shape.EnumerateArray().Select(dimension => dimension.ValueKind == JsonValueKind.Number && dimension.TryGetInt64(out long value) ? value : 0)];
            }
        }

        return (tensors, metadata);
    }

    private static (ImageModelFamily Family, string Description) Base(Dictionary<string, long[]> tensors, Dictionary<string, string> metadata, string? civitaiBase)
    {
        string declared = string.Join(" ", new[]
        {
            metadata.GetValueOrDefault("ss_base_model_version"),
            metadata.GetValueOrDefault("modelspec.architecture"),
            civitaiBase
        }.OfType<string>()).ToLowerInvariant();

        bool declaresFlux2 = Regex.IsMatch(declared, @"flux[\s._-]*2");
        bool declaresKlein4B = declaresFlux2 && declared.Contains("klein", StringComparison.Ordinal) && !declared.Contains("9b", StringComparison.Ordinal);
        bool declaresFlux1 = Regex.IsMatch(declared, @"flux[\s._-]*1") || (declared.Contains("flux", StringComparison.Ordinal) && !declaresFlux2);

        // Stable Diffusion: the width the cross-attention keys read.
        long context = 0;
        long textWidth = 0;
        bool twoTextEncoders = false;
        bool fluxBlocks = false;
        bool perBlockModulation = false;
        long fluxWidth = 0;
        int maxDouble = -1;
        int maxSingle = -1;

        foreach ((string name, long[] shape) in tensors)
        {
            bool down = name.Contains("lora_down", StringComparison.Ordinal) || name.Contains("lora.down", StringComparison.Ordinal) || name.Contains("lora_A", StringComparison.Ordinal);

            if (down && shape.Length == 2 && name.Contains("attn2", StringComparison.Ordinal) && (name.Contains("to_k", StringComparison.Ordinal)))
            {
                context = Math.Max(context, shape[1]);
            }

            if (down && shape.Length == 2 && (name.StartsWith("lora_te_", StringComparison.Ordinal) || name.StartsWith("lora_te1_", StringComparison.Ordinal)) && name.Contains("k_proj", StringComparison.Ordinal))
            {
                textWidth = Math.Max(textWidth, shape[1]);
            }

            // kohya names SD 1.x's one text encoder lora_te_, and SDXL's two lora_te1_ and lora_te2_, even when only the
            // first is trained.
            twoTextEncoders |= name.StartsWith("lora_te1_", StringComparison.Ordinal) || name.StartsWith("lora_te2_", StringComparison.Ordinal)
                || name.Contains("text_encoder_2", StringComparison.Ordinal) || name.StartsWith("te2.", StringComparison.Ordinal);

            Match block = FluxBlock().Match(name);

            if (block.Success)
            {
                fluxBlocks = true;
                int index = int.Parse(block.Groups["index"].Value, CultureInfo.InvariantCulture);

                if (block.Groups["kind"].Value.StartsWith("single", StringComparison.Ordinal))
                {
                    maxSingle = Math.Max(maxSingle, index);
                }
                else
                {
                    maxDouble = Math.Max(maxDouble, index);
                }

                perBlockModulation |= name.Contains("img_mod", StringComparison.Ordinal) || name.Contains("txt_mod", StringComparison.Ordinal)
                    || name.Contains("norm1.linear", StringComparison.Ordinal) || name.Contains("norm1_linear", StringComparison.Ordinal)
                    || name.Contains("modulation_lin", StringComparison.Ordinal) || name.Contains("modulation.lin", StringComparison.Ordinal) || name.Contains("norm.linear", StringComparison.Ordinal);

                if (down && shape.Length == 2)
                {
                    fluxWidth = Math.Max(fluxWidth, shape[1]);
                }
            }
        }

        if (context == SDXL_CONTEXT || (context == 0 && twoTextEncoders))
        {
            return (ImageModelFamily.StableDiffusionXL, "SDXL");
        }

        if (context == SD1_CONTEXT || (context == 0 && textWidth == SD1_CONTEXT))
        {
            return (ImageModelFamily.StableDiffusion1, "Stable Diffusion 1.x");
        }

        if (context == SD2_CONTEXT || (context == 0 && textWidth == SD2_CONTEXT))
        {
            return (ImageModelFamily.StableDiffusion2, "Stable Diffusion 2.x");
        }

        if (fluxBlocks)
        {
            if (declaresKlein4B)
            {
                return (ImageModelFamily.Flux2Klein4B, "FLUX.2 [klein] 4B");
            }

            if (declaresFlux1 || perBlockModulation || maxDouble >= 5 || maxSingle >= 20)
            {
                return (ImageModelFamily.OtherImageModel, "FLUX.1");
            }

            if (declaresFlux2 || (fluxWidth != 0 && fluxWidth != FLUX_WIDTH))
            {
                return (ImageModelFamily.OtherImageModel, "A larger FLUX.2 model");
            }

            // Without metadata, only a LoRA over exactly klein 4B's blocks is taken for one: a FLUX.1 LoRA trained on its
            // first few blocks looks the same, and is never applied to a model it may not fit.
            return maxDouble == KLEIN_4B_DOUBLE_BLOCKS - 1 && maxSingle == KLEIN_4B_SINGLE_BLOCKS - 1
                ? (ImageModelFamily.Flux2Klein4B, "FLUX.2 [klein] 4B")
                : (ImageModelFamily.Unknown, "A FLUX LoRA whose base could not be told");
        }

        // No tensors this app recognizes; the metadata may still say what it is.
        if (declared.Contains("xl", StringComparison.Ordinal) || RETRAINED_SDXL.Any(word => declared.Contains(word, StringComparison.Ordinal)))
        {
            return (ImageModelFamily.StableDiffusionXL, "SDXL");
        }

        if (Regex.IsMatch(declared, @"sd[\s._-]*v?1|stable-diffusion-v1|\b1\.5\b"))
        {
            return (ImageModelFamily.StableDiffusion1, "Stable Diffusion 1.x");
        }

        if (declaresKlein4B)
        {
            return (ImageModelFamily.Flux2Klein4B, "FLUX.2 [klein] 4B");
        }

        return declared.Length == 0
            ? (ImageModelFamily.Unknown, "Unknown base")
            : (ImageModelFamily.OtherImageModel, Describe(declared));
    }

    private static string Describe(string declared)
    {
        string text = declared.Trim();
        return text.Length > 40 ? text[..40] + "…" : text;
    }

    private static string? Note(ImageModelFamily family, Dictionary<string, string> metadata, string? civitaiBase)
    {
        if (family != ImageModelFamily.StableDiffusionXL)
        {
            return null;
        }

        string trained = string.Join(" ", new[]
        {
            civitaiBase,
            metadata.GetValueOrDefault("ss_sd_model_name"),
            metadata.GetValueOrDefault("ss_base_model"),
            metadata.GetValueOrDefault("modelspec.title")
        }.OfType<string>()).ToLowerInvariant();

        string? retrained = RETRAINED_SDXL.FirstOrDefault(word => trained.Contains(word, StringComparison.Ordinal));

        return retrained is null
            ? null
            : $"Trained on {CultureInfo.InvariantCulture.TextInfo.ToTitleCase(retrained)}, an SDXL model retrained for tag-style prompts. It loads, but may suit DreamShaper XL poorly.";
    }

    private static (string? Words, string? Source) TriggerWords(Dictionary<string, string> metadata, Sidecar sidecar)
    {
        if (!string.IsNullOrWhiteSpace(sidecar.CivitaiWords))
        {
            return (Tidy(sidecar.CivitaiWords), "its Civitai info");
        }

        if (!string.IsNullOrWhiteSpace(sidecar.ActivationText))
        {
            return (Tidy(sidecar.ActivationText), "its Stable Diffusion web UI notes");
        }

        if (metadata.GetValueOrDefault("modelspec.trigger_phrase") is { } phrase && !string.IsNullOrWhiteSpace(phrase))
        {
            return (Tidy(phrase), "the file's metadata");
        }

        if (metadata.GetValueOrDefault("ss_tag_frequency") is { } frequency && FromCaptions(frequency) is { } captions)
        {
            return (captions, "its training captions");
        }

        return (null, null);
    }

    /// <summary>
    /// The tags in nearly every training caption, most frequent first, without generic ones: up to three.
    /// </summary>
    internal static string? FromCaptions(string tagFrequency)
    {
        Dictionary<string, long> counts = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            using JsonDocument document = JsonDocument.Parse(tagFrequency);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (JsonProperty folder in document.RootElement.EnumerateObject().Where(folder => folder.Value.ValueKind == JsonValueKind.Object))
            {
                foreach (JsonProperty tag in folder.Value.EnumerateObject())
                {
                    if (tag.Value.ValueKind == JsonValueKind.Number && tag.Value.TryGetInt64(out long count))
                    {
                        string name = tag.Name.Trim();
                        counts[name] = counts.GetValueOrDefault(name) + count;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        if (counts.Count == 0)
        {
            return null;
        }

        long most = counts.Values.Max();

        if (most < 2)
        {
            return null;
        }

        string[] triggers = [.. counts
            .Where(tag => tag.Value * 10 >= most * 9 && tag.Key.Length > 0 && !GENERIC_TAGS.Contains(tag.Key) && !IsSentence(tag.Key))
            .OrderByDescending(tag => tag.Value)
            .ThenBy(tag => tag.Key, StringComparer.Ordinal)
            .Take(3)
            .Select(tag => tag.Key)];

        return triggers.Length == 0 ? null : string.Join(", ", triggers);
    }

    /// <summary>
    /// Whether a caption "tag" is a whole sentence: LoRAs captioned in plain language count each caption as one tag, and a
    /// caption repeated across a small training set is no trigger word.
    /// </summary>
    private static bool IsSentence(string tag)
    {
        return tag.Length > 40 || tag.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 4;
    }

    private static string Tidy(string words)
    {
        return Whitespace().Replace(words, " ").Trim().TrimEnd(',', ';').Trim();
    }

    /// <summary>
    /// The notes other tools keep beside a LoRA: Civitai's model info, and the Stable Diffusion web UI's user metadata.
    /// </summary>
    private sealed record Sidecar(string? BaseModel, string? CivitaiWords, string? ActivationText, float? Strength);

    private static Sidecar ReadSidecars(string path)
    {
        string stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        string? baseModel = null;
        string? civitaiWords = null;
        string? activation = null;
        float? strength = null;

        if (ReadJson(stem + ".civitai.info") is { } civitai)
        {
            using (civitai)
            {
                if (civitai.RootElement.TryGetProperty("baseModel", out JsonElement model) && model.ValueKind == JsonValueKind.String)
                {
                    baseModel = model.GetString();
                }

                if (civitai.RootElement.TryGetProperty("trainedWords", out JsonElement words) && words.ValueKind == JsonValueKind.Array)
                {
                    string[] trained = [.. words.EnumerateArray().Where(word => word.ValueKind == JsonValueKind.String).Select(word => word.GetString()!.Trim()).Where(word => word.Length > 0)];
                    civitaiWords = trained.Length == 0 ? null : string.Join(", ", trained);
                }
            }
        }

        if (ReadJson(stem + ".json") is { } webUi)
        {
            using (webUi)
            {
                if (webUi.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (webUi.RootElement.TryGetProperty("activation text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                    {
                        activation = text.GetString();
                    }

                    // Only a number, or a number in text; anything else (null, true, an object) is no weight.
                    float number = 0;

                    if (webUi.RootElement.TryGetProperty("preferred weight", out JsonElement weight)
                        && weight.ValueKind switch
                        {
                            JsonValueKind.Number => weight.TryGetSingle(out number),
                            JsonValueKind.String => float.TryParse(weight.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number),
                            _ => false
                        }
                        && number > 0 && number <= ImageLoraSettings.MAX_STRENGTH)
                    {
                        strength = number;
                    }

                    if (baseModel is null && webUi.RootElement.TryGetProperty("sd version", out JsonElement version) && version.ValueKind == JsonValueKind.String)
                    {
                        baseModel = version.GetString();
                    }
                }
            }
        }

        return new Sidecar(baseModel, civitaiWords, activation, strength);
    }

    private static JsonDocument? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024)
            {
                return null;
            }

            JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A FLUX transformer block in a LoRA tensor's name, in any of the naming schemes: kohya's
    /// <c>lora_unet_double_blocks_0_</c>, ComfyUI's <c>double_blocks.0.</c>, or diffusers'
    /// <c>transformer_blocks.0.</c> and <c>single_transformer_blocks.0.</c>.
    /// </summary>
    [GeneratedRegex(@"(?<kind>double_blocks|single_blocks|single_transformer_blocks|(?<!single_)transformer_blocks)[._](?<index>\d+)[._]")]
    private static partial Regex FluxBlock();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
