using HomiliaMessenger.Integrations.LocalModels;
using LLama.Native;
using System.Collections.Generic;

namespace HomiliaMessenger.Integrations.Embeddings;

/// <summary>
/// An embedding model the application knows how to download and run with llama.cpp. Embedding models are a category of
/// their own: they share the GGUF format and runtime with conversational models, but are never offered as an agent.
/// </summary>
/// <param name="Id">
/// Recorded with every vector the model makes. It names the exact file, so vectors from another file are never compared.
/// </param>
/// <param name="BaseUrl">Where the file is downloaded from, pinned to a specific revision so the hash stays valid.</param>
/// <param name="Dimensions">The length of the vectors the model makes; anything else it returns is refused.</param>
/// <param name="Pooling">How the model's per-token outputs are combined into one vector, as the model was trained.</param>
/// <param name="ContextTokens">The most tokens embedded at once; longer texts are shortened first.</param>
/// <param name="QueryPrefix">Written before a search, as the model's authors recommend.</param>
/// <param name="DocumentPrefix">Written before what is searched, as the model's authors recommend.</param>
/// <param name="MinSimilarity">
/// The cosine similarity below which the model's matches are unrelated: midway between the least related and the most
/// unrelated pair measured on a small set of memories and questions (see <c>EmbeddingModelTests</c>), since models
/// differ widely in how they score.
/// </param>
internal sealed record EmbeddingModelDefinition(string Id, string DisplayName, string Description, string License, string BaseUrl, LocalModelFile File, int Dimensions, LLamaPoolingType Pooling, int ContextTokens, string QueryPrefix, string DocumentPrefix, float MinSimilarity)
{
    public IReadOnlyList<LocalModelFile> Files => [File];
}

/// <summary>
/// The embedding models offered for download. Both are small enough to stay loaded beside a conversational model.
/// </summary>
internal static class EmbeddingModelCatalog
{
    internal const string BGE_SMALL = "bge-small-en-v1.5-q8_0";
    internal const string NOMIC_EMBED = "nomic-embed-text-v1.5-q4_k_m";

    internal static IReadOnlyList<EmbeddingModelDefinition> Models { get; } =
    [
        new(
            BGE_SMALL,
            "BGE small (English)",
            "BAAI's small English embedding model. A quick download and fast on any CPU; a good fit for most computers.",
            "MIT",
            "https://huggingface.co/CompendiumLabs/bge-small-en-v1.5-gguf/resolve/d32f8c040ea3b516330eeb75b72bcc2d3a780ab7/",
            new("bge-small-en-v1.5-q8_0.gguf", 36806944, "ec38e8da142596baa913124ae50550de284b6916bf59577ef2f0cb9660c2f514"),
            384,
            LLamaPoolingType.CLS,
            512,
            "Represent this sentence for searching relevant passages: ",
            string.Empty,
            // Measured: related 0.52, unrelated up to 0.43.
            0.48f),
        new(
            NOMIC_EMBED,
            "Nomic Embed Text v1.5",
            "Nomic's larger embedding model. Finds related memories more reliably, for a larger download and somewhat slower searches.",
            "Apache 2.0",
            "https://huggingface.co/nomic-ai/nomic-embed-text-v1.5-GGUF/resolve/0188c9bf409793f810680a5a431e7b899c46104c/",
            new("nomic-embed-text-v1.5.Q4_K_M.gguf", 84106624, "d4e388894e09cf3816e8b0896d81d265b55e7a9fff9ab03fe8bf4ef5e11295ac"),
            768,
            LLamaPoolingType.Mean,
            512,
            "search_query: ",
            "search_document: ",
            // Measured: related 0.63, unrelated up to 0.54.
            0.59f)
    ];
}
