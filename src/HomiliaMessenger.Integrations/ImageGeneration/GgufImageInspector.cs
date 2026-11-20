using HomiliaMessenger.Business.Modules.ImageGeneration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HomiliaMessenger.Integrations.ImageGeneration;

/// <summary>
/// What a GGUF file's header shows about the model in it.
/// </summary>
/// <param name="NeedsBytes">
/// When the header continues past the bytes given, how many bytes to read in all (at least); otherwise zero.
/// </param>
/// <param name="Architecture">The architecture the file declares in its metadata, when it declares one.</param>
internal sealed record GgufInspection(ImageModelFamily Family, bool HasTextEncoder, bool HasVae, string? Quantization, string? Architecture, string? Problem, long NeedsBytes = 0)
{
    public bool IsSupported => Problem is null && NeedsBytes == 0;

    public static GgufInspection Incomplete(long bytes)
    {
        return new(ImageModelFamily.Unknown, false, false, null, null, null, bytes);
    }

    public static GgufInspection Unrecognized(string problem)
    {
        return new(ImageModelFamily.Unknown, false, false, null, null, problem);
    }
}

/// <summary>
/// Reads the header of a GGUF file (its metadata and the names, shapes, and types of its tensors, not the weights) to
/// tell what image model it holds: a self-contained Stable Diffusion 1.x or SDXL checkpoint (the UNet, the CLIP text
/// encoder, and the VAE), or a FLUX.2 [klein] 4B diffusion model, which is read with a separate text encoder and VAE. A
/// file's name and repository say nothing reliable about this; its tensors do. Whether the app can run it on the processor
/// or only on a graphics card is decided by the caller.
/// </summary>
/// <remarks>
/// Tensor names follow the original Stable Diffusion checkpoint layout that stable-diffusion.cpp writes and reads
/// (<c>model.diffusion_model.*</c>, <c>cond_stage_model.*</c>, <c>first_stage_model.*</c>). Stable Diffusion 1.x
/// conditions its UNet on 768-dimensional CLIP ViT-L/14 features, 2.x on 1024-dimensional OpenCLIP features, and SDXL
/// adds a class embedding; the cross-attention key projection's input width tells them apart. FLUX.2 has modulation shared
/// by its double-stream blocks (<c>double_stream_modulation_img</c>), unlike FLUX.1, and the width of its text input
/// (<c>txt_in</c>) is three of its text encoder's layers: 7680 for [klein] 4B's Qwen3-4B (3 × 2560), larger for the
/// models read with Qwen3-8B or Mistral. Files that declare a language model's architecture in their metadata are
/// classified by it. The GGUF layout is from the
/// <a href="https://github.com/ggml-org/ggml/blob/master/docs/gguf.md">GGUF specification</a>.
/// </remarks>
internal static class GgufImageInspector
{
    internal const string SUPPORTED = "This app runs Stable Diffusion 1.x models such as DreamShaper 8, and on a graphics card also SDXL models and FLUX.2 [klein] 4B.";

    /// <summary>
    /// The width of FLUX.2 [klein] 4B's text input: three layers of Qwen3-4B's 2560-wide hidden states.
    /// </summary>
    internal const long KLEIN_4B_TEXT_WIDTH = 7680;

    private const uint GGUF_MAGIC = 0x46554747;
    private const int MAX_NAME_BYTES = 64 * 1024;

    /// <summary>
    /// Architectures that image-model converters declare; their tensors tell which this app can run.
    /// </summary>
    private static readonly HashSet<string> IMAGE_ARCHITECTURES = new(StringComparer.OrdinalIgnoreCase)
    {
        "sd1", "sdxl", "sd3", "flux", "flux2", "aura", "ltxv", "hyvid", "wan", "hidream", "cosmos", "lumina2", "qwen_image", "chroma", "sana", "pixart"
    };

    private static readonly Dictionary<uint, string> TYPE_NAMES = new()
    {
        [0] = "F32",
        [1] = "F16",
        [2] = "Q4_0",
        [3] = "Q4_1",
        [6] = "Q5_0",
        [7] = "Q5_1",
        [8] = "Q8_0",
        [9] = "Q8_1",
        [10] = "Q2_K",
        [11] = "Q3_K",
        [12] = "Q4_K",
        [13] = "Q5_K",
        [14] = "Q6_K",
        [15] = "Q8_K",
        [16] = "IQ2_XXS",
        [17] = "IQ2_XS",
        [18] = "IQ3_XXS",
        [19] = "IQ1_S",
        [20] = "IQ4_NL",
        [21] = "IQ3_S",
        [22] = "IQ2_S",
        [23] = "IQ4_XS",
        [29] = "IQ1_M",
        [30] = "BF16",
        [34] = "TQ1_0",
        [35] = "TQ2_0"
    };

    /// <summary>
    /// Inspects the start of a GGUF file. When <paramref name="header"/> ends before the header does, the result says
    /// how many bytes to read and inspect again.
    /// </summary>
    internal static GgufInspection Inspect(ReadOnlySpan<byte> header)
    {
        Reader reader = new(header);

        try
        {
            if (reader.UInt32() != GGUF_MAGIC)
            {
                return GgufInspection.Unrecognized("This is not a GGUF file.");
            }

            uint version = reader.UInt32();

            if (version is < 2 or > 3)
            {
                return GgufInspection.Unrecognized($"GGUF version {version} is not supported.");
            }

            long tensorCount = reader.Int64();
            long metadataCount = reader.Int64();
            string? architecture = null;

            for (long index = 0; index < metadataCount; index++)
            {
                string key = reader.String();
                uint type = reader.UInt32();

                if (key == "general.architecture" && type == 8)
                {
                    architecture = reader.String();

                    if (!IMAGE_ARCHITECTURES.Contains(architecture))
                    {
                        // A language model, classified by its declared architecture; the rest of its header can be
                        // megabytes of vocabulary.
                        return ByArchitecture(architecture);
                    }
                }
                else
                {
                    reader.SkipValue(type);
                }
            }

            return ByTensors(ref reader, tensorCount, architecture);
        }
        catch (NeedMoreBytesException needed)
        {
            return GgufInspection.Incomplete(needed.Bytes);
        }
        catch (FormatException error)
        {
            return GgufInspection.Unrecognized("The file's header could not be read: " + error.Message);
        }
    }

    private static GgufInspection ByArchitecture(string architecture)
    {
        return new(ImageModelFamily.LanguageModel, false, false, null, architecture,
            $"This is a language model ({architecture}), not an image model. Conversation models are added under Local Models.");
    }

    private static GgufInspection ByTensors(ref Reader reader, long tensorCount, string? architecture)
    {
        bool hasUnet = false;
        bool hasClassEmbedding = false;
        bool hasTextEncoder = false;
        bool hasVae = false;
        bool hasOtherImageBlocks = false;
        bool hasLanguageModelTensors = false;
        bool hasFlux2Modulation = false;
        long textInputWidth = 0;
        long contextWidth = 0;
        long inputChannels = 0;
        Dictionary<uint, long> types = [];

        for (long index = 0; index < tensorCount; index++)
        {
            string name = reader.String();
            uint dimensions = reader.UInt32();

            if (dimensions > 8)
            {
                throw new FormatException($"tensor {name} has {dimensions} dimensions.");
            }

            long[] shape = new long[dimensions];

            for (int dimension = 0; dimension < dimensions; dimension++)
            {
                shape[dimension] = reader.Int64();
            }

            uint type = reader.UInt32();
            reader.Int64();
            types[type] = types.GetValueOrDefault(type) + 1;

            if (name.Contains("input_blocks.", StringComparison.Ordinal) || name.Contains("middle_block.", StringComparison.Ordinal))
            {
                hasUnet = true;

                if (contextWidth == 0 && name.EndsWith("attn2.to_k.weight", StringComparison.Ordinal) && shape.Length >= 1)
                {
                    contextWidth = shape[0];
                }

                if (name.EndsWith("input_blocks.0.0.weight", StringComparison.Ordinal) && shape.Length == 4)
                {
                    inputChannels = shape[2];
                }
            }

            hasClassEmbedding |= name.Contains("diffusion_model.label_emb.", StringComparison.Ordinal);
            hasTextEncoder |= name.Contains("text_model.embeddings.token_embedding", StringComparison.Ordinal)
                || name.Contains("cond_stage_model.model.token_embedding", StringComparison.Ordinal)
                || name.StartsWith("conditioner.embedders.", StringComparison.Ordinal);
            hasVae |= name.StartsWith("first_stage_model.decoder.", StringComparison.Ordinal) || name.StartsWith("vae.decoder.", StringComparison.Ordinal);
            hasOtherImageBlocks |= name.Contains("double_blocks.", StringComparison.Ordinal) || name.Contains("joint_blocks.", StringComparison.Ordinal)
                || name.Contains("single_transformer_blocks.", StringComparison.Ordinal);
            hasLanguageModelTensors |= name.StartsWith("blk.", StringComparison.Ordinal) || name == "token_embd.weight";
            hasFlux2Modulation |= name.Contains("double_stream_modulation_img.", StringComparison.Ordinal);

            // GGUF lists a matrix's input width first.
            if (name.EndsWith("txt_in.weight", StringComparison.Ordinal) && shape.Length == 2)
            {
                textInputWidth = shape[0];
            }
        }

        string? quantization = Quantization(types);

        if (hasUnet && contextWidth > 0)
        {
            if (hasClassEmbedding || contextWidth == 2048)
            {
                return new(ImageModelFamily.StableDiffusionXL, hasTextEncoder, hasVae, quantization, architecture, CheckpointProblem(hasTextEncoder, hasVae, inputChannels));
            }

            if (contextWidth == 1024)
            {
                return new(ImageModelFamily.StableDiffusion2, hasTextEncoder, hasVae, quantization, architecture, "This is a Stable Diffusion 2.x model. " + SUPPORTED);
            }

            if (contextWidth == 768)
            {
                return new(ImageModelFamily.StableDiffusion1, hasTextEncoder, hasVae, quantization, architecture, CheckpointProblem(hasTextEncoder, hasVae, inputChannels));
            }
        }

        if (hasFlux2Modulation)
        {
            return textInputWidth == KLEIN_4B_TEXT_WIDTH
                ? new(ImageModelFamily.Flux2Klein4B, false, false, quantization, architecture, null)
                : new(ImageModelFamily.OtherImageModel, false, false, quantization, architecture,
                    "This is a larger FLUX.2 model, read with a text encoder other than Qwen3-4B. Of FLUX.2, only [klein] 4B models are supported.");
        }

        if (hasOtherImageBlocks)
        {
            return new(ImageModelFamily.OtherImageModel, hasTextEncoder, hasVae, quantization, architecture, "This image model family is not supported. " + SUPPORTED);
        }

        if (hasLanguageModelTensors)
        {
            return new(ImageModelFamily.LanguageModel, false, false, quantization, architecture,
                "This is a language model, not an image model. Conversation models are added under Local Models.");
        }

        if (architecture is not null && !architecture.StartsWith("sd", StringComparison.OrdinalIgnoreCase))
        {
            return new(ImageModelFamily.OtherImageModel, hasTextEncoder, hasVae, quantization, architecture, $"This is a {architecture} image model. " + SUPPORTED);
        }

        return new(ImageModelFamily.Unknown, hasTextEncoder, hasVae, quantization, architecture,
            hasVae && !hasUnet
                ? "This file holds only a VAE, not a complete image model."
                : "The file's tensors were not recognized as an image model this app runs. " + SUPPORTED);
    }

    /// <summary>
    /// Why a Stable Diffusion checkpoint cannot be used on its own, or <see langword="null"/>.
    /// </summary>
    private static string? CheckpointProblem(bool hasTextEncoder, bool hasVae, long inputChannels)
    {
        if (inputChannels is not (0 or 4))
        {
            return "This is an inpainting or image-editing model, which needs a source image; it cannot draw a portrait from a description alone.";
        }

        if (!hasTextEncoder || !hasVae)
        {
            string missing = !hasTextEncoder && !hasVae ? "text encoder or VAE" : !hasTextEncoder ? "text encoder" : "VAE";
            return $"This file has no {missing}; it holds only part of the model. Only self-contained files, with the text encoder and VAE included, are supported.";
        }

        return null;
    }

    /// <summary>
    /// The type most of the quantized tensors use, or F16 or F32 when none is quantized.
    /// </summary>
    private static string? Quantization(Dictionary<uint, long> types)
    {
        KeyValuePair<uint, long>[] quantized = [.. types.Where(type => type.Key is not (0 or 1 or 30))];

        if (quantized.Length > 0)
        {
            uint dominant = quantized.MaxBy(type => type.Value).Key;
            return TYPE_NAMES.GetValueOrDefault(dominant, $"type {dominant}");
        }

        return types.ContainsKey(1) ? "F16" : types.ContainsKey(30) ? "BF16" : types.ContainsKey(0) ? "F32" : null;
    }

    private sealed class NeedMoreBytesException : Exception
    {
        public NeedMoreBytesException(long bytes)
        {
            Bytes = bytes;
        }

        public long Bytes { get; }
    }

    /// <summary>
    /// Reads little-endian GGUF values, asking for more bytes when the data given ends.
    /// </summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public Reader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public uint UInt32()
        {
            return BitConverter.ToUInt32(Take(4));
        }

        public long Int64()
        {
            return BitConverter.ToInt64(Take(8));
        }

        public string String()
        {
            long length = Int64();

            if (length is < 0 or > MAX_NAME_BYTES)
            {
                throw new FormatException($"a string is {length} bytes long.");
            }

            return Encoding.UTF8.GetString(Take((int)length));
        }

        public void SkipValue(uint type)
        {
            switch (type)
            {
                case 0 or 1 or 7:
                    Take(1);
                    break;
                case 2 or 3:
                    Take(2);
                    break;
                case 4 or 5 or 6:
                    Take(4);
                    break;
                case 10 or 11 or 12:
                    Take(8);
                    break;
                case 8:
                    String();
                    break;
                case 9:
                    uint elementType = UInt32();
                    long count = Int64();

                    if (count < 0)
                    {
                        throw new FormatException("an array has a negative length.");
                    }

                    for (long index = 0; index < count; index++)
                    {
                        SkipValue(elementType);
                    }

                    break;
                default:
                    throw new FormatException($"metadata type {type} is unknown.");
            }
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (_position + count > _data.Length)
            {
                // Ask for at least twice as much, so a long header is read in a few requests rather than many.
                throw new NeedMoreBytesException(Math.Max((long)_data.Length * 2, (long)_position + count));
            }

            ReadOnlySpan<byte> slice = _data.Slice(_position, count);
            _position += count;
            return slice;
        }
    }
}
