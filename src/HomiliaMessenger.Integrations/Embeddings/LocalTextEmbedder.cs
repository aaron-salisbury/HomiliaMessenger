using HomiliaMessenger.Business.Modules.Memories;
using LLama.Exceptions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.Embeddings;

/// <summary>
/// Embeds text with the embedding model chosen in Settings, on this device through llama.cpp. Nothing is sent anywhere:
/// until a model is downloaded and chosen, embedding is unavailable and memories are recalled by their words.
/// </summary>
internal sealed class LocalTextEmbedder : ITextEmbedder
{
    private readonly EmbeddingModelLibrary _library;
    private readonly LlamaEmbeddingHost? _host;
    private readonly ILogger<LocalTextEmbedder> _logger;

    public LocalTextEmbedder(EmbeddingModelLibrary library, ILogger<LocalTextEmbedder> logger, LlamaEmbeddingHost? host = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _host = host;
    }

    public string? Model => _host is null ? null : _library.Active?.Id;

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        if (_host is null || _library.Active is not { } model)
        {
            return EmbeddingResult.Unavailable("No embedding model is downloaded. Download one in Settings to search memories by meaning.");
        }

        if (texts.Count == 0)
        {
            return EmbeddingResult.Success(model.Id, [], model.MinSimilarity);
        }

        string prefix = purpose == EmbeddingPurpose.Query ? model.QueryPrefix : model.DocumentPrefix;

        try
        {
            IReadOnlyList<float[]> vectors = await _host.EmbedAsync(_library.ModelDirectory(model), model, [.. texts.Select(text => prefix + text)], cancellationToken);

            if (vectors.Any(vector => vector.Length != model.Dimensions))
            {
                _logger.LogWarning("Embedding model {ModelId} returned vectors of an unexpected length.", model.Id);
                return EmbeddingResult.Failed($"{model.DisplayName} returned vectors of an unexpected length.");
            }

            return EmbeddingResult.Success(model.Id, vectors, model.MinSimilarity);
        }
        catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException)
        {
            // Removed while the request was waiting its turn.
            return EmbeddingResult.Unavailable($"{model.DisplayName} has not been downloaded yet. Download it from Settings.");
        }
        catch (Exception error) when (error is RuntimeError or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            _logger.LogError(error, "Embedding model {ModelId} failed.", model.Id);
            return EmbeddingResult.Failed($"{model.DisplayName} could not run: {error.Message}");
        }
    }
}
