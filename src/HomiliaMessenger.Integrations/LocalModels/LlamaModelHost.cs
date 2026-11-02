using HomiliaMessenger.Business.Modules.Agents;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.LocalModels;

/// <summary>
/// Keeps one GGUF model loaded in llama.cpp and runs generations on it one at a time, as <see cref="OnnxModelHost"/> does
/// for ONNX models: loaded on first use, released after a period without use, before its files are deleted, or at
/// shutdown; and the prompt's cache kept between replies, so only new tokens are read.
/// </summary>
internal sealed class LlamaModelHost : IAsyncDisposable, IDisposable
{
    internal static readonly TimeSpan IDLE_UNLOAD_AFTER = TimeSpan.FromMinutes(10);

    // The prompt cache is sized from the model so that it stays within this much memory, between these bounds.
    internal const long CACHE_BUDGET_BYTES = 768L * 1024 * 1024;
    internal const int MIN_CONTEXT_TOKENS = 1024;
    internal const int MAX_CONTEXT_TOKENS = 4096;

    private const uint BATCH_SIZE = 512;

    // Reading a long prompt cannot be interrupted, so shutdown waits up to this long for a generation to stop.
    private static readonly TimeSpan SHUTDOWN_WAIT = TimeSpan.FromSeconds(30);

    private static readonly Lock _loggingLock = new();
    private static bool _loggingConfigured;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LlamaModelHost> _logger;
    private readonly ITimer _idleTimer;
    private readonly List<LLamaToken> _cached = [];
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private string? _loadedDirectory;
    private DateTimeOffset _lastUsed;
    private bool _disposed;

    public LlamaModelHost(ILogger<LlamaModelHost> logger) : this(logger, TimeProvider.System)
    {
    }

    internal LlamaModelHost(ILogger<LlamaModelHost> logger, TimeProvider timeProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _idleTimer = _timeProvider.CreateTimer(_ => _ = UnloadIfIdleAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The context size to use for a model: as much as fits in <see cref="CACHE_BUDGET_BYTES"/> of 16-bit cache, between
    /// <see cref="MIN_CONTEXT_TOKENS"/> and <see cref="MAX_CONTEXT_TOKENS"/>, and no more than the model was trained on.
    /// </summary>
    internal static int ContextTokens(int layers, int heads, int keyValueHeads, int embeddingSize, int trainedContext)
    {
        int headSize = heads > 0 ? embeddingSize / heads : embeddingSize;
        long bytesPerToken = Math.Max(1L, 2L * layers * Math.Max(keyValueHeads, 1) * headSize * sizeof(short));
        long fits = CACHE_BUDGET_BYTES / bytesPerToken;
        long limit = trainedContext > 0 ? Math.Min(MAX_CONTEXT_TOKENS, trainedContext) : MAX_CONTEXT_TOKENS;

        return (int)Math.Max(Math.Min(fits, limit), Math.Min(MIN_CONTEXT_TOKENS, limit)) / 256 * 256;
    }

    /// <summary>
    /// Formats <paramref name="messages"/> with the model's chat template and generates the assistant's next message.
    /// When the prompt would not fit, the oldest half of the conversation is dropped (repeatedly if needed).
    /// </summary>
    /// <returns>The reply, or <see langword="null"/> when even the latest message does not fit.</returns>
    /// <exception cref="FileNotFoundException">The model's file is missing, for example because it was removed.</exception>
    internal async Task<string?> GenerateAsync(string modelDirectory, string modelFile, IReadOnlyList<ChatTemplateMessage> messages, AgentRequest request, GenerationSettings settings, CancellationToken cancellationToken)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _gate.WaitAsync(stop.Token).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Generation is CPU-bound native work; keep it off the caller's thread. Continuations must not need the UI
            // thread, which has stopped by the time the application disposes this host at exit.
            return await Task.Run(() => Generate(modelDirectory, modelFile, messages, request, settings, stop.Token), stop.Token).ConfigureAwait(false);
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
    /// Releases the model loaded from <paramref name="modelDirectory"/> (or any model, when it is <see langword="null"/>),
    /// waiting for any generation to finish, then runs <paramref name="whileUnloaded"/> before another generation can load
    /// it again.
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

    /// <summary>
    /// Stops any generation in progress and releases the model once it has stopped.
    /// </summary>
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

        // Otherwise a generation is still reading a prompt; the process is exiting, which reclaims the model's memory.
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private string? Generate(string modelDirectory, string modelFile, IReadOnlyList<ChatTemplateMessage> messages, AgentRequest request, GenerationSettings settings, CancellationToken cancellationToken)
    {
        if (!string.Equals(_loadedDirectory, modelDirectory, StringComparison.OrdinalIgnoreCase))
        {
            UnloadCore();
            Load(modelDirectory, modelFile);
        }

        LLamaWeights weights = _weights!;
        LLamaContext context = _context!;
        int contextTokens = (int)context.ContextSize;
        LLamaToken[]? prompt = FitPrompt(weights, context, messages, contextTokens - settings.MaxReplyTokens);

        if (prompt is null)
        {
            return null;
        }

        try
        {
            int logitsIndex = ReadPrompt(context, prompt);
            using ISamplingPipeline sampler = request.Precise
                ? new GreedySamplingPipeline()
                : new DefaultSamplingPipeline { Temperature = (float)settings.Temperature, TopP = (float)settings.TopP, TopK = settings.TopK, RepeatPenalty = (float)settings.RepetitionPenalty };
            StreamingTokenDecoder decoder = new(context);
            StringBuilder reply = new();
            LLamaBatch batch = new();
            bool finished = false;

            for (int count = 0; count < settings.MaxReplyTokens && _cached.Count < contextTokens; count++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LLamaToken token = sampler.Sample(context.NativeHandle, logitsIndex);
                sampler.Accept(token);

                if (token.IsEndOfGeneration(weights.Vocab))
                {
                    finished = true;
                    break;
                }

                decoder.Add(token);
                reply.Append(decoder.Read());

                // Stop as soon as the model starts writing the next turn itself.
                string text = reply.ToString();
                int end = request.EndOf(text);

                if (end < text.Length)
                {
                    reply.Length = end;
                    finished = true;
                    break;
                }

                batch.Clear();
                batch.Add(token, _cached.Count, LLamaSeqId.Zero, true);
                Decode(context, batch);
                _cached.Add(token);
                logitsIndex = 0;
            }

            return finished ? reply.ToString() : OnnxModelHost.WithoutUnfinishedEnd(reply.ToString());
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A failed generation leaves the cache in an unknown state. A canceled one stopped between tokens, so its
            // cache stays reusable for the reply that replaced it.
            ClearCache();
            throw;
        }
    }

    private void Load(string modelDirectory, string modelFile)
    {
        string path = Path.Combine(modelDirectory, modelFile);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The model file '{path}' does not exist.", path);
        }

        ConfigureLogging(_logger);

        // Read the model's shape first, to size the prompt cache before the context is created.
        ModelParams parameters = new(path) { GpuLayerCount = 0, BatchSize = BATCH_SIZE };

        try
        {
            _weights = LLamaWeights.LoadFromFile(parameters);
            SafeLlamaModelHandle shape = _weights.NativeHandle;
            parameters.ContextSize = (uint)ContextTokens(shape.LayerCount, shape.HeadCount, shape.KVHeadCount, shape.EmbeddingSize, shape.ContextSize);
            _context = _weights.CreateContext(parameters);
        }
        catch
        {
            // Release what did load, such as the weights when there is no memory left for the context, so the memory
            // is freed and the file can be deleted.
            UnloadCore();
            throw;
        }

        _loadedDirectory = modelDirectory;
        _cached.Clear();
        _logger.LogInformation("Loaded GGUF model {ModelFile} with a {ContextTokens}-token context.", modelFile, parameters.ContextSize);
    }

    /// <summary>
    /// The prompt's tokens, dropping the oldest half of the conversation until it fits within <paramref name="budget"/>.
    /// Dropping in halves keeps the start of the prompt stable for several replies, so the cache stays reusable.
    /// </summary>
    private static LLamaToken[]? FitPrompt(LLamaWeights weights, LLamaContext context, IReadOnlyList<ChatTemplateMessage> messages, int budget)
    {
        List<ChatTemplateMessage> fitted = [.. messages];

        while (true)
        {
            LLamaTemplate template = new(weights) { AddAssistant = true };

            foreach (ChatTemplateMessage message in fitted)
            {
                template.Add(message.Role, message.Content);
            }

            LLamaToken[] tokens = WithoutDuplicateStart(context.Tokenize(Encoding.UTF8.GetString(template.Apply()), addBos: true, special: true), weights);

            if (tokens.Length <= budget)
            {
                return tokens;
            }

            int conversation = fitted.Count - 1;

            if (conversation <= 1)
            {
                return null;
            }

            fitted.RemoveRange(1, conversation / 2);

            // Chat templates expect the conversation to start with the user.
            while (fitted.Count > 2 && fitted[1].Role != "user")
            {
                fitted.RemoveAt(1);
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="prompt"/> into the cache, reusing the tokens it shares with what is cached, and returns the
    /// batch index of the last token's predictions.
    /// </summary>
    private int ReadPrompt(LLamaContext context, LLamaToken[] prompt)
    {
        int reusable = 0;
        int limit = Math.Min(_cached.Count, prompt.Length - 1);

        while (reusable < limit && _cached[reusable] == prompt[reusable])
        {
            reusable++;
        }

        context.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, reusable, -1);

        // Some architectures (recurrent or hybrid ones) cannot drop the end of their cache. If it still reaches past the
        // tokens being reused, start over from an empty cache rather than reply from a state the tokens do not describe.
        if (context.NativeHandle.MemorySequenceMaxPosition(LLamaSeqId.Zero).Value != reusable - 1)
        {
            ClearCache();
            reusable = 0;
        }

        _cached.RemoveRange(reusable, _cached.Count - reusable);

        LLamaBatch batch = new();

        for (int start = reusable; start < prompt.Length; start += (int)BATCH_SIZE)
        {
            int end = Math.Min(start + (int)BATCH_SIZE, prompt.Length);
            batch.Clear();

            for (int index = start; index < end; index++)
            {
                batch.Add(prompt[index], index, LLamaSeqId.Zero, index == prompt.Length - 1);
            }

            Decode(context, batch);
            _cached.AddRange(prompt[start..end]);
        }

        return batch.TokenCount - 1;
    }

    /// <summary>
    /// Some chat templates write the start-of-text token themselves, which tokenizing then adds again; keep only one.
    /// </summary>
    internal static LLamaToken[] WithoutDuplicateStart(LLamaToken[] tokens, LLamaWeights weights)
    {
        return tokens.Length > 1 && tokens[0] == tokens[1] && tokens[0].IsControl(weights.Vocab) ? tokens[1..] : tokens;
    }

    private static void Decode(LLamaContext context, LLamaBatch batch)
    {
        DecodeResult result = context.Decode(batch);

        if (result != DecodeResult.Ok)
        {
            throw new InvalidOperationException($"The model could not read its input ({result}).");
        }
    }

    private async Task UnloadIfIdleAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            // In use; the timer restarts when the generation finishes.
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

    private void ClearCache()
    {
        _context?.NativeHandle.MemoryClear(true);
        _cached.Clear();
    }

    private void UnloadCore()
    {
        _context?.Dispose();
        _weights?.Dispose();
        _context = null;
        _weights = null;
        _loadedDirectory = null;
        _cached.Clear();
    }

    /// <summary>
    /// Sends llama.cpp's own messages to the application log, warnings and errors only, rather than the console. This
    /// must happen before the native library loads, so once, before the first model.
    /// </summary>
    private static void ConfigureLogging(ILogger logger)
    {
        lock (_loggingLock)
        {
            if (_loggingConfigured)
            {
                return;
            }

            _loggingConfigured = true;

            NativeLibraryConfig.All.WithLogCallback((level, message) =>
            {
                if (level is LLamaLogLevel.Warning or LLamaLogLevel.Error)
                {
                    logger.Log(level == LLamaLogLevel.Error ? LogLevel.Error : LogLevel.Warning, "llama.cpp: {Message}", message.TrimEnd());
                }
            });
        }
    }
}
