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
/// tell whether it holds a Stable Diffusion 1.x model this app can run on its own: the UNet, the CLIP text encoder, and
/// the VAE. A file's name and repository say nothing reliable about this; its tensors do.
/// </summary>
/// <remarks>
/// Tensor names follow the original Stable Diffusion checkpoint layout that stable-diffusion.cpp writes and reads
/// (<c>model.diffusion_model.*</c>, <c>cond_stage_model.*</c>, <c>first_stage_model.*</c>). Stable Diffusion 1.x
/// conditions its UNet on 768-dimensional CLIP ViT-L/14 features, 2.x on 1024-dimensional OpenCLIP features, and SDXL
/// adds a class embedding; the cross-attention key projection's input width tells them apart. Files that declare an
/// architecture in their metadata, as language models and some UNet-only image conversions do, are classified by it.
/// The GGUF layout is from the <a href="https://github.com/ggml-org/ggml/blob/master/docs/gguf.md">GGUF specification</a>.
/// </remarks>
internal static class GgufImageInspector
{
    internal const string ONLY_SD1 = "Only Stable Diffusion 1.x models, such as DreamShaper 8, are supported.";

    private const uint GGUF_MAGIC = 0x46554747;
    private const int MAX_NAME_BYTES = 64 * 1024;

    /// <summary>
    /// Architectures that image-model converters declare, which are not Stable Diffusion 1.x.
    /// </summary>
    private static readonly HashSet<string> OTHER_IMAGE_ARCHITECTURES = new(StringComparer.OrdinalIgnoreCase)
    {
        "sd3", "flux", "aura", "ltxv", "hyvid", "wan", "hidream", "cosmos", "lumina2", "qwen_image", "chroma", "sana", "pixart"
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

                    if (!architecture.Equals("sd1", StringComparison.OrdinalIgnoreCase) && !architecture.Equals("sdxl", StringComparison.OrdinalIgnoreCase))
                    {
                        // Classified by its declared architecture; the rest of a language model's header can be megabytes
                        // of vocabulary.
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
        if (OTHER_IMAGE_ARCHITECTURES.Contains(architecture))
        {
            return new(ImageModelFamily.OtherImageModel, false, false, null, architecture, $"This is a {architecture} image model. " + ONLY_SD1);
        }

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
        }

        string? quantization = Quantization(types);

        if (hasUnet && contextWidth > 0)
        {
            if (hasClassEmbedding || contextWidth == 2048)
            {
                return new(ImageModelFamily.StableDiffusionXL, hasTextEncoder, hasVae, quantization, architecture, "This is an SDXL model. " + ONLY_SD1);
            }

            if (contextWidth == 1024)
            {
                return new(ImageModelFamily.StableDiffusion2, hasTextEncoder, hasVae, quantization, architecture, "This is a Stable Diffusion 2.x model. " + ONLY_SD1);
            }

            if (contextWidth == 768)
            {
                return new(ImageModelFamily.StableDiffusion1, hasTextEncoder, hasVae, quantization, architecture, Sd1Problem(hasTextEncoder, hasVae, inputChannels));
            }
        }

        if (hasOtherImageBlocks)
        {
            return new(ImageModelFamily.OtherImageModel, hasTextEncoder, hasVae, quantization, architecture, "This image model family is not supported. " + ONLY_SD1);
        }

        if (hasLanguageModelTensors)
        {
            return new(ImageModelFamily.LanguageModel, false, false, quantization, architecture,
                "This is a language model, not an image model. Conversation models are added under Local Models.");
        }

        return new(ImageModelFamily.Unknown, hasTextEncoder, hasVae, quantization, architecture,
            hasVae && !hasUnet
                ? "This file holds only a VAE, not a complete image model."
                : "The file's tensors were not recognized as a Stable Diffusion 1.x model.");
    }

    private static string? Sd1Problem(bool hasTextEncoder, bool hasVae, long inputChannels)
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
