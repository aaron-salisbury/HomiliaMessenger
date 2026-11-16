using HomiliaMessenger.Integrations.LocalModels;
using LLama;
using LLama.Common;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.Embeddings;

/// <summary>
/// Keeps one embedding model loaded in llama.cpp and embeds texts with it one request at a time: loaded on first use,
/// released after a period without use, before its file is deleted, or at shutdown. It is separate from the
/// conversational model host, so searching memories never unloads the contact's model, and the reverse.
/// </summary>
internal sealed class LlamaEmbeddingHost : IAsyncDisposable, IDisposable
{
    internal static readonly TimeSpan IDLE_UNLOAD_AFTER = TimeSpan.FromMinutes(10);

    // Embedding a few memories takes well under a second, so shutdown need not wait long.
    private static readonly TimeSpan SHUTDOWN_WAIT = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LlamaEmbeddingHost> _logger;
    private readonly ITimer _idleTimer;
    private LLamaWeights? _weights;
    private LLamaEmbedder? _embedder;
    private string? _loadedDirectory;
    private DateTimeOffset _lastUsed;
    private bool _disposed;

    public LlamaEmbeddingHost(ILogger<LlamaEmbeddingHost> logger) : this(logger, TimeProvider.System)
    {
    }

    internal LlamaEmbeddingHost(ILogger<LlamaEmbeddingHost> logger, TimeProvider timeProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _idleTimer = _timeProvider.CreateTimer(_ => _ = UnloadIfIdleAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// One unit-length vector per text, in order. Texts are shortened to fit the model's context first.
    /// </summary>
    /// <exception cref="FileNotFoundException">The model's file is missing, for example because it was removed.</exception>
    internal async Task<IReadOnlyList<float[]>> EmbedAsync(string modelDirectory, EmbeddingModelDefinition model, IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _gate.WaitAsync(stop.Token).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Embedding is CPU-bound native work; keep it off the caller's thread.
            return await Task.Run(() => EmbedCoreAsync(modelDirectory, model, texts, stop.Token), stop.Token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (!_shutdown.IsCancellationRequested)
                {
                    _lastUsed = _timeProvider.GetUtcNow();
                    _idleTimer.Change(IDLE_UNLOAD_AFTER, Timeout.InfiniteTimeSpan);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// Releases the model loaded from <paramref name="modelDirectory"/>, waiting for any embedding to finish, then runs
    /// <paramref name="whileUnloaded"/> before another request can load it again.
    /// </summary>
    internal async Task UnloadAsync(string? modelDirectory, Action? whileUnloaded = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (modelDirectory is null || string.Equals(_loadedDirectory, modelDirectory, StringComparison.OrdinalIgnoreCase))
            {
                UnloadCore();
            }

            whileUnloaded?.Invoke();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _shutdown.Cancel();
        bool stopped = await _gate.WaitAsync(SHUTDOWN_WAIT).ConfigureAwait(false);
        _disposed = true;
        _idleTimer.Dispose();

        if (stopped)
        {
            UnloadCore();
            _gate.Release();
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// <paramref name="text"/> shortened to what fits in <paramref name="contextTokens"/>, at about two characters a token,
    /// which leaves room for text that tokenizes poorly. Memories and searches are far shorter than this.
    /// </summary>
    internal static string Fit(string text, int contextTokens)
    {
        int limit = Math.Max(1, contextTokens * 2);

        return text.Length <= limit ? text : text[..limit];
    }

    /// <summary>
    /// <paramref name="vector"/> scaled to unit length, so cosine similarity is unaffected by how long a text was.
    /// </summary>
    internal static float[] Normalized(float[] vector)
    {
        double squares = 0;

        foreach (float value in vector)
        {
            squares += value * (double)value;
        }

        if (squares == 0)
        {
            return vector;
        }

        float scale = (float)(1 / Math.Sqrt(squares));
        float[] normalized = new float[vector.Length];

        for (int index = 0; index < vector.Length; index++)
        {
            normalized[index] = vector[index] * scale;
        }

        return normalized;
    }

    private async Task<IReadOnlyList<float[]>> EmbedCoreAsync(string modelDirectory, EmbeddingModelDefinition model, IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (!string.Equals(_loadedDirectory, modelDirectory, StringComparison.OrdinalIgnoreCase))
        {
            UnloadCore();
            Load(modelDirectory, model);
        }

        LLamaEmbedder embedder = _embedder!;
        List<float[]> vectors = [];

        foreach (string text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<float[]> embedded = await embedder.GetEmbeddings(Fit(text, model.ContextTokens), cancellationToken).ConfigureAwait(false);

            if (embedded.Count != 1)
            {
                throw new InvalidOperationException($"The embedding model returned {embedded.Count} vectors for one text.");
            }

            vectors.Add(Normalized(embedded[0]));
        }

        return vectors;
    }

    private void Load(string modelDirectory, EmbeddingModelDefinition model)
    {
        string path = Path.Combine(modelDirectory, model.File.LocalName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The embedding model file '{path}' does not exist.", path);
        }

        LlamaModelHost.ConfigureLogging(_logger);

        // Embedding models read the whole text at once, so a batch must hold the longest text.
        uint tokens = (uint)model.ContextTokens;
        ModelParams parameters = new(path)
        {
            GpuLayerCount = 0,
            ContextSize = tokens,
            BatchSize = tokens,
            UBatchSize = tokens,
            Embeddings = true,
            PoolingType = model.Pooling
        };

        try
        {
            _weights = LLamaWeights.LoadFromFile(parameters);
            _embedder = new LLamaEmbedder(_weights, parameters, _logger);
        }
        catch
        {
            UnloadCore();
            throw;
        }

        _loadedDirectory = modelDirectory;
        _logger.LogInformation("Loaded embedding model {ModelId}.", model.Id);
    }

    private async Task UnloadIfIdleAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (!_disposed && _timeProvider.GetUtcNow() - _lastUsed >= IDLE_UNLOAD_AFTER)
            {
                UnloadCore();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void UnloadCore()
    {
        _embedder?.Dispose();
        _weights?.Dispose();
        _embedder = null;
        _weights = null;
        _loadedDirectory = null;
    }
}
