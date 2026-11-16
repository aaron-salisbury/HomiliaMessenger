using HomiliaMessenger.Business.Modules.Agents;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Memories;

/// <summary>
/// What a text is embedded for. Some embedding models are trained to embed a search differently from what is searched.
/// </summary>
public enum EmbeddingPurpose
{
    /// <summary>
    /// Text that is stored and searched, such as a memory.
    /// </summary>
    Document,

    /// <summary>
    /// Text searched with, such as the user's latest message.
    /// </summary>
    Query
}

public enum EmbeddingStatus
{
    Success,

    /// <summary>
    /// No embedding model is ready, for example because none has been downloaded.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The model could not embed the texts, or returned vectors that cannot be used.
    /// </summary>
    Failed
}

/// <summary>
/// The outcome of embedding texts. On success, <see cref="Vectors"/> holds one vector per text, in order, all from
/// <see cref="Model"/>; <see cref="Problem"/> explains any other status.
/// </summary>
/// <param name="MinSimilarity">
/// The cosine similarity below which this model's matches are unrelated, when the provider knows it. Models differ
/// widely: one may score unrelated texts higher than another scores related ones.
/// </param>
public sealed record EmbeddingResult(EmbeddingStatus Status, string? Model, IReadOnlyList<float[]> Vectors, string? Problem, float? MinSimilarity = null)
{
    public static EmbeddingResult Success(string model, IReadOnlyList<float[]> vectors, float? minSimilarity = null)
    {
        return new(EmbeddingStatus.Success, model, vectors, null, minSimilarity);
    }

    public static EmbeddingResult Unavailable(string problem)
    {
        return new(EmbeddingStatus.Unavailable, null, [], problem);
    }

    public static EmbeddingResult Failed(string problem)
    {
        return new(EmbeddingStatus.Failed, null, [], problem);
    }
}

/// <summary>
/// Capability to turn text into vectors whose cosine similarity reflects how alike the texts' meanings are. Separate from
/// conversation: a model that converses is not assumed to embed, and the reverse.
/// </summary>
public interface ITextEmbedder
{
    /// <summary>
    /// The model texts are embedded with now, identifying which stored vectors are comparable; <see langword="null"/> when
    /// no embedding model is ready.
    /// </summary>
    string? Model { get; }

    Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken cancellationToken = default);
}

/// <summary>
/// Used when no embedding provider is registered: memories are then recalled by keywords.
/// </summary>
internal sealed class UnavailableTextEmbedder : ITextEmbedder
{
    public string? Model => null;

    public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(EmbeddingResult.Unavailable("No embedding model is available."));
    }
}

/// <summary>
/// An embedding model that can run on this device, kept apart from conversational models: it cannot be a contact's
/// agent, and choosing it decides how every contact's memories are searched.
/// </summary>
/// <param name="IsSelected">Whether memories are searched with it, once it is downloaded.</param>
public sealed record EmbeddingModel(string Id, string DisplayName, string Description, string License, long DownloadBytes, bool IsDownloaded, bool IsSelected);

/// <summary>
/// Capability to manage the embedding models that run on this device and choose the one memories are searched with.
/// </summary>
public interface IEmbeddingModelLibrary
{
    IReadOnlyList<EmbeddingModel> List();

    /// <summary>
    /// Downloads and verifies a model. When no model is chosen yet, the downloaded one is chosen.
    /// </summary>
    Task<LocalModelDownloadResult> DownloadAsync(string modelId, IProgress<LocalModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string modelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Chooses the model memories are searched with. Memories embedded by another model are embedded again in the
    /// background; their text is never changed. Returns <see langword="false"/> for an unknown model.
    /// </summary>
    bool Select(string modelId);
}
