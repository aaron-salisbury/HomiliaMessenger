using HomiliaMessenger.Business.Modules.Memories;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Memories;

/// <summary>
/// A deterministic stand-in for an embedding model: each distinct word has a dimension of its own, so texts sharing words
/// are similar and texts with none in common are not. Needs no model or network.
/// </summary>
internal sealed class FakeEmbedder : ITextEmbedder
{
    internal const string MODEL = "fake-embedder";
    internal const int DIMENSIONS = 4096;

    // Shared by every instance, so the same word always has the same dimension.
    private static readonly ConcurrentDictionary<string, int> VOCABULARY = new(StringComparer.Ordinal);
    private static int _lastIndex = -1;

    public string? Model { get; set; } = MODEL;

    /// <summary>
    /// When set, returned instead of embedding.
    /// </summary>
    public EmbeddingResult? Result { get; set; }

    /// <summary>
    /// When set, thrown instead of embedding.
    /// </summary>
    public Exception? Error { get; set; }

    /// <summary>
    /// The length of the vectors made, to imitate a model returning the wrong size.
    /// </summary>
    public int Dimensions { get; set; } = DIMENSIONS;

    /// <summary>
    /// The threshold this model reports for unrelated matches.
    /// </summary>
    public float? MinSimilarity { get; set; } = 0.3f;

    public List<EmbeddingPurpose> Purposes { get; } = [];

    public List<string> Texts { get; } = [];

    public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Purposes.Add(purpose);
        Texts.AddRange(texts);

        if (Error is not null)
        {
            throw Error;
        }

        if (Result is not null)
        {
            return Task.FromResult(Result);
        }

        if (Model is not { } model)
        {
            return Task.FromResult(EmbeddingResult.Unavailable("No model."));
        }

        return Task.FromResult(EmbeddingResult.Success(model, [.. texts.Select(Vector)], MinSimilarity));
    }

    internal float[] Vector(string text)
    {
        float[] vector = new float[Dimensions];

        if (Dimensions == 0)
        {
            return vector;
        }

        foreach (string word in MemoryRecall.Words(text))
        {
            int index = VOCABULARY.GetOrAdd(word, _ => Interlocked.Increment(ref _lastIndex));
            vector[index % (Dimensions - 1)] += 1;
        }

        // A text with no words still needs a usable vector.
        vector[^1] += 0.01f;

        return vector;
    }
}
