using HomiliaMessenger.Business.Modules.Agents;
using Microsoft.ML.OnnxRuntimeGenAI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.LocalModels;

/// <summary>
/// A message in the chat-template input: <c>system</c>, <c>user</c>, or <c>assistant</c>.
/// </summary>
internal sealed record ChatTemplateMessage([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string Content);

/// <summary>
/// Sampling for conversational replies: varied enough to sound natural, short enough to read like a text message. The
/// reply limit also bounds how long a reply takes on a CPU.
/// </summary>
internal sealed record GenerationSettings(int MaxReplyTokens = 80, double Temperature = 0.8, double TopP = 0.9, int TopK = 40, double RepetitionPenalty = 1.05);

/// <summary>
/// Keeps one local model loaded and runs generations on it one at a time. A model holds gigabytes of memory, so it is
/// loaded on first use and released after a period without use, before its files are deleted, or at shutdown. Every
/// use of the native model happens while holding <c>_gate</c>, so it is never released mid-generation.
/// </summary>
/// <remarks>
/// Reading a prompt is the slow part on a CPU (tens of tokens per second), so the model's attention cache is kept between
/// replies. A chat's next prompt repeats the previous one plus the latest messages, so only the new tokens are read,
/// turning a 20-second wait in a long chat into about two seconds. The cache holds one chat at a time.
/// </remarks>
internal sealed class OnnxModelHost : IAsyncDisposable, IDisposable
{
    internal static readonly TimeSpan IDLE_UNLOAD_AFTER = TimeSpan.FromMinutes(10);

    // Reading a long prompt cannot be interrupted, so shutdown waits up to this long for a generation to stop.
    private static readonly TimeSpan SHUTDOWN_WAIT = TimeSpan.FromSeconds(30);

    // ONNX Runtime GenAI must be shut down explicitly before the process exits; otherwise its native threads can leave
    // the process hanging partway through exiting. Disposing this handle performs that shutdown, once, for the process.
    private static readonly Lock _runtimeLock = new();
    private static OgaHandle? _runtime;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _idleTimer;
    private Model? _model;
    private Tokenizer? _tokenizer;
    private int[] _endTokens = [];
    private GeneratorParams? _generatorParams;
    private Generator? _generator;
    private bool _generatorPrecise;
    private string? _loadedDirectory;
    private DateTimeOffset _lastUsed;
    private bool _disposed;

    public OnnxModelHost() : this(TimeProvider.System)
    {
    }

    internal OnnxModelHost(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _idleTimer = _timeProvider.CreateTimer(_ => _ = UnloadIfIdleAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Formats <paramref name="messages"/> with the model's own chat template and generates the assistant's next message.
    /// When the prompt would not fit in <paramref name="contextTokens"/>, the oldest half of the conversation is dropped
    /// (repeatedly if needed), keeping the system message and the latest messages.
    /// </summary>
    /// <returns>The reply, or <see langword="null"/> when even the latest message does not fit.</returns>
    /// <exception cref="DirectoryNotFoundException">The model's files are missing, for example because it was removed.</exception>
    internal async Task<string?> GenerateAsync(string modelDirectory, int contextTokens, IReadOnlyList<ChatTemplateMessage> messages, AgentRequest request, GenerationSettings settings, CancellationToken cancellationToken)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _gate.WaitAsync(stop.Token).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Generation is CPU-bound native work; keep it off the caller's thread. Continuations must not need the UI
            // thread, which has stopped by the time the application disposes this host at exit.
            return await Task.Run(() => Generate(modelDirectory, contextTokens, messages, request, settings, stop.Token), stop.Token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                // Shutdown disposes the timer once this generation releases the gate, so it is never rearmed after.
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
    /// waiting for any generation to finish, then runs <paramref name="whileUnloaded"/> before any other generation can
    /// load it again.
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

    private string? Generate(string modelDirectory, int contextTokens, IReadOnlyList<ChatTemplateMessage> messages, AgentRequest request, GenerationSettings settings, CancellationToken cancellationToken)
    {
        if (!string.Equals(_loadedDirectory, modelDirectory, StringComparison.OrdinalIgnoreCase))
        {
            UnloadCore();

            if (!Directory.Exists(modelDirectory))
            {
                throw new DirectoryNotFoundException($"The model folder '{modelDirectory}' does not exist.");
            }

            EnsureRuntimeShutsDownAtExit();
            _model = new Model(modelDirectory);
            _tokenizer = new Tokenizer(_model);
            _endTokens = _tokenizer.GetEosTokenIds().ToArray();
            _loadedDirectory = modelDirectory;
        }

        int[]? prompt = FitPrompt(messages, contextTokens - settings.MaxReplyTokens);

        if (prompt is null)
        {
            return null;
        }

        try
        {
            Generator generator = ContinueFrom(prompt, contextTokens, settings, request.Precise);
            using TokenizerStream stream = _tokenizer!.CreateStream();
            StringBuilder reply = new();
            bool finished = false;

            for (int count = 0; count < settings.MaxReplyTokens && !generator.IsDone(); count++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                generator.GenerateNextToken();
                int token = generator.GetNextTokens()[0];

                if (_endTokens.Contains(token))
                {
                    finished = true;
                    break;
                }

                reply.Append(stream.Decode(token));

                // Stop as soon as the model starts writing the next turn itself.
                string text = reply.ToString();
                int end = request.EndOf(text);

                if (end < text.Length)
                {
                    reply.Length = end;
                    finished = true;
                    break;
                }
            }

            return finished ? reply.ToString() : WithoutUnfinishedEnd(reply.ToString());
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A failed generation leaves the cache in an unknown state. A canceled one stopped between tokens, so its
            // cache stays reusable for the reply that replaced it.
            DisposeGenerator();
            throw;
        }
    }

    /// <summary>
    /// A reply cut off by the length limit, ended at its last complete sentence or line rather than mid-word.
    /// </summary>
    internal static string WithoutUnfinishedEnd(string text)
    {
        int end = text.TrimEnd().LastIndexOfAny(['.', '!', '?', '…', '\n']);

        return end > 0 ? text[..(end + 1)].TrimEnd() : text;
    }

    /// <summary>
    /// The prompt's tokens, dropping the oldest half of the conversation until it fits within <paramref name="budget"/>.
    /// Dropping in halves keeps the start of the prompt stable for several replies, so the cache stays reusable.
    /// </summary>
    private int[]? FitPrompt(IReadOnlyList<ChatTemplateMessage> messages, int budget)
    {
        List<ChatTemplateMessage> fitted = [.. messages];

        while (true)
        {
            string prompt = _tokenizer!.ApplyChatTemplate(null, JsonSerializer.Serialize(fitted), null, true);
            using Sequences sequences = _tokenizer.Encode(prompt);

            if (sequences[0].Length <= budget)
            {
                return sequences[0].ToArray();
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
    /// Readies the generator to reply to <paramref name="prompt"/>, reusing the cached tokens it shares with the previous
    /// prompt and reply, and reading only the rest.
    /// </summary>
    private Generator ContinueFrom(int[] prompt, int contextTokens, GenerationSettings settings, bool precise)
    {
        int reusable = 0;

        if (_generator is not null && _generator.TokenCount() > 0 && _generatorParams!.GetSearchNumber("max_length") == contextTokens && _generatorPrecise == precise)
        {
            ReadOnlySpan<int> cached = _generator.GetSequence(0);

            // The last generated token is in the sequence but has not been read into the cache yet.
            int limit = Math.Min(cached.Length - 1, prompt.Length - 1);

            while (reusable < limit && cached[reusable] == prompt[reusable])
            {
                reusable++;
            }
        }

        if (reusable == 0)
        {
            DisposeGenerator();
            _generatorParams = new GeneratorParams(_model!);
            // The cache is allocated for the whole context up front, so the context size bounds memory use.
            _generatorParams.SetSearchOption("max_length", contextTokens);
            // A precise request takes the most likely token each time; others sample for natural variety.
            _generatorParams.SetSearchOption("do_sample", !precise);
            _generatorPrecise = precise;
            _generatorParams.SetSearchOption("temperature", settings.Temperature);
            _generatorParams.SetSearchOption("top_p", settings.TopP);
            _generatorParams.SetSearchOption("top_k", settings.TopK);
            _generatorParams.SetSearchOption("repetition_penalty", settings.RepetitionPenalty);
            _generator = new Generator(_model!, _generatorParams);
        }
        else
        {
            _generator!.RewindTo((ulong)reusable);
        }

        _generator!.AppendTokens(prompt.AsSpan(reusable));

        return _generator;
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

    private static void EnsureRuntimeShutsDownAtExit()
    {
        lock (_runtimeLock)
        {
            if (_runtime is not null)
            {
                return;
            }

            _runtime = new OgaHandle();
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                lock (_runtimeLock)
                {
                    _runtime?.Dispose();
                    _runtime = null;
                }
            };
        }
    }

    private void DisposeGenerator()
    {
        _generator?.Dispose();
        _generatorParams?.Dispose();
        _generator = null;
        _generatorParams = null;
    }

    private void UnloadCore()
    {
        DisposeGenerator();
        _tokenizer?.Dispose();
        _model?.Dispose();
        _tokenizer = null;
        _model = null;
        _endTokens = [];
        _loadedDirectory = null;
    }
}
