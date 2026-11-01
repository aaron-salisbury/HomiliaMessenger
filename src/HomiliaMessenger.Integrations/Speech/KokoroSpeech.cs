using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Speech;
using HomiliaMessenger.Integrations.Downloads;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using KokoroSharp.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.Speech;

internal sealed record KokoroDownload(string Url, long Bytes, string Sha256);

/// <summary>Owns Kokoro downloads, configuration, inference, and audio output. No worker process is needed.</summary>
internal sealed class KokoroSpeech : ITextToSpeech, IAsyncDisposable
{
    internal const long MODEL_BYTES = 325508342;
    internal const string MODEL_SHA256 = "0cfd5e79aab70a3d8c1a57dc639835110ddb32c9f5ff4fdd1f4db202ea43bb05";
    private const string MODEL_URL = "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx";
    private readonly string _directory;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<KokoroSpeech> _logger;
    private readonly Func<float[], CancellationToken, Task> _play;
    private readonly KokoroDownload _download;
    private readonly object _playbackLock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource _playbackStop = new();
    private KokoroModel? _model;

    // Voices loaded for this model, so each reply does not read and parse its voice file again.
    private readonly Dictionary<string, KokoroVoice> _voices = new(StringComparer.Ordinal);
    private SessionOptions? _options;
    private volatile bool _enabled;
    private bool _disposed;

    private string ModelPath => Path.Combine(_directory, "kokoro.onnx");
    private string EnabledPath => Path.Combine(_directory, "enabled");

    public SpeechModel Model => new("Kokoro v1.0 (English)", _download.Bytes, IsDownloaded(), _enabled);

    // English voices are bundled with KokoroSharp. They all share the one downloaded model.
    public IReadOnlyList<SpeechVoice> Voices { get; } = CreateVoices();

    public KokoroSpeech(string directory, IHttpClientFactory http, ILogger<KokoroSpeech> logger)
        : this(directory, http, logger, PlayAsync)
    {
    }

    internal KokoroSpeech(string directory, IHttpClientFactory http, ILogger<KokoroSpeech> logger, Func<float[], CancellationToken, Task> play, KokoroDownload? download = null)
    {
        _directory = directory;
        _http = http;
        _logger = logger;
        _play = play;
        _download = download ?? new(MODEL_URL, MODEL_BYTES, MODEL_SHA256);
        _enabled = File.Exists(EnabledPath);
    }

    private bool IsDownloaded()
    {
        FileInfo file = new(ModelPath);
        return file.Exists && file.Length == _download.Bytes;
    }

    /// <summary>
    /// Downloads and verifies the model. A failed, stalled, or canceled download keeps what arrived, and the next attempt
    /// resumes it.
    /// </summary>
    public async Task<SpeechResult> DownloadAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _gate.WaitAsync(stop.Token).ConfigureAwait(false);
        string partial = ModelPath + ".download";

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsDownloaded())
            {
                return SpeechResult.Success;
            }

            Directory.CreateDirectory(_directory);
            long downloaded = Math.Min(FileDownload.ExistingLength(partial), _download.Bytes);

            if (FileDownload.MissingSpace(_directory, _download.Bytes - downloaded, "Kokoro") is { } problem)
            {
                return new(false, problem);
            }

            DownloadProgress reporter = new(progress is null ? null : (bytes, total) => progress.Report(bytes * 100d / total), _download.Bytes, downloaded);
            using HttpClient client = _http.CreateClient();
            await FileDownload.DownloadAsync(client, new Uri(_download.Url), partial, _download.Bytes, reporter, stop.Token).ConfigureAwait(false);
            reporter.Report(force: true);

            if (FileDownload.ExistingLength(partial) != _download.Bytes
                || !string.Equals(await FileDownload.HashAsync(partial, stop.Token).ConfigureAwait(false), _download.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                return new(false, "The Kokoro download failed its integrity check and was discarded. Please try again.");
            }

            File.Move(partial, ModelPath, overwrite: true);
            return SpeechResult.Success;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException)
        {
            _logger.LogWarning(error, "Could not download Kokoro");
            return new(false, $"Could not download Kokoro: {error.Message} Try again to resume.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled)
        {
            CancelPlayback();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (enabled)
            {
                if (!IsDownloaded())
                {
                    throw new InvalidOperationException("Download Kokoro before enabling speech.");
                }

                await File.WriteAllTextAsync(EnabledPath, "Kokoro v1.0", cancellationToken).ConfigureAwait(false);
                lock (_playbackLock)
                {
                    if (_playbackStop.IsCancellationRequested)
                    {
                        _playbackStop.Dispose();
                        _playbackStop = new();
                    }
                }
            }
            else
            {
                File.Delete(EnabledPath);
                Unload();
            }

            _enabled = enabled;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        CancelPlayback();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            File.Delete(EnabledPath);
            _enabled = false;
            Unload();
            File.Delete(ModelPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SpeechResult> SpeakAsync(string text, string voiceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!Model.IsAvailable)
            {
                return new(false, "Enable Kokoro in Settings to hear replies.");
            }

            if (!Voices.Any(voice => voice.Id == voiceId))
            {
                return new(false, "This speech voice is unavailable. Choose a voice on the Contacts page.");
            }

            string speechText = SpeechText.Prepare(text);
            if (string.IsNullOrWhiteSpace(speechText))
            {
                return SpeechResult.Success;
            }

            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token, _playbackStop.Token);
            // Native inference and phonemization must never run on the UI thread. Use the core model API so failures
            // propagate to this task instead of escaping a library-owned background thread.
            await Task.Run(async () =>
            {
                stop.Token.ThrowIfCancellationRequested();
                if (_model is null)
                {
                    _options = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 2 };
                    _model = new KokoroModel(ModelPath, _options);
                }

                if (!_voices.TryGetValue(voiceId, out KokoroVoice? voice))
                {
                    voice = KokoroVoice.FromPath(Path.Combine(AppContext.BaseDirectory, "SpeechVoices", voiceId + ".npy"));
                    _voices[voiceId] = voice;
                }

                int[] tokens = Tokenizer.Tokenize(speechText, voiceId[0] == 'b' ? "en-gb" : "en-us");

                foreach (int[] segment in Segments(tokens))
                {
                    stop.Token.ThrowIfCancellationRequested();
                    float[] samples = _model.Infer(segment, voice.Features);
                    stop.Token.ThrowIfCancellationRequested();
                    await _play(samples, stop.Token).ConfigureAwait(false);
                }
            }, stop.Token).ConfigureAwait(false);
            return SpeechResult.Success;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Speech was turned off or removed in Settings, or the app is closing; the reply simply stops.
            return SpeechResult.Success;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(error, "Could not voice a reply with Kokoro");
            Unload();
            return new(false, "Could not voice the reply: " + error.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Splits a reply's tokens at punctuation, so long replies are not cut mid-word and the first, short segment starts
    /// playing sooner. Any segment still over the model's limit is split further.
    /// </summary>
    internal static IEnumerable<int[]> Segments(int[] tokens)
    {
        foreach (int[] segment in SegmentationSystem.SplitToSegments(tokens, new DefaultSegmentationConfig()))
        {
            foreach (int[] part in segment.Chunk(KokoroModel.maxTokens))
            {
                yield return part;
            }
        }
    }

    private static async Task PlayAsync(float[] samples, CancellationToken cancellationToken)
    {
        if (samples.Length == 0)
        {
            return;
        }

        byte[] pcm = KokoroPlayback.GetBytes(samples);
        using RawSourceWaveStream stream = new(pcm, 0, pcm.Length, new WaveFormat(24000, 16, 1));
        KokoroWaveOutEvent player = CrossPlatformHelper.GetAudioPlayer();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            player.Init(stream);
            player.Play();
            while (player.PlaybackState == PlaybackState.Playing)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            player.Dispose();
        }
    }

    private void Unload()
    {
        _voices.Clear();
        _model?.Dispose();
        _model = null;
        _options?.Dispose();
        _options = null;
    }

    private void CancelPlayback()
    {
        lock (_playbackLock)
        {
            if (!_disposed)
            {
                _playbackStop.Cancel();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed)
            {
                _disposed = true;
                Unload();
                lock (_playbackLock)
                {
                    _playbackStop.Dispose();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<SpeechVoice> CreateVoices()
    {
        List<SpeechVoice> voices = [];
        Add("af", Gender.Female, "American", "alloy", "aoede", "bella", "heart", "jessica", "kore", "nicole", "nova", "river", "sarah", "sky");
        Add("am", Gender.Male, "American", "adam", "echo", "eric", "fenrir", "liam", "michael", "onyx", "puck", "santa");
        Add("bf", Gender.Female, "British", "alice", "emma", "isabella", "lily");
        Add("bm", Gender.Male, "British", "daniel", "fable", "george", "lewis");
        return voices;

        void Add(string prefix, Gender gender, string accent, params string[] names)
        {
            foreach (string name in names)
            {
                voices.Add(new SpeechVoice(prefix + "_" + name, char.ToUpperInvariant(name[0]) + name[1..] + $" ({accent}, {gender.ToString().ToLowerInvariant()})", gender));
            }
        }
    }
}
