using HomiliaMessenger.Business.Modules.Speech;
using HomiliaMessenger.Integrations.Speech;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Speech;

[TestClass]
public sealed class KokoroSpeechTests
{
    private static readonly byte[] MODEL = Encoding.UTF8.GetBytes("fake model for download tests");
    private static readonly KokoroDownload DOWNLOAD = new("https://speech.example/kokoro.onnx", MODEL.Length, Convert.ToHexString(SHA256.HashData(MODEL)));
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Download_VerifiesBeforeInstallingAndConfigurationPersistsAcrossInstances()
    {
        using TestFolder folder = new();
        using Server server = new(MODEL);
        await using KokoroSpeech speech = Create(folder.Path, server);
        Assert.IsFalse(speech.Model.IsAvailable);
        SpeechResult missing = await speech.SpeakAsync("Hello", "af_heart", TestContext.CancellationToken);
        Assert.IsFalse(missing.IsSuccessful);
        Assert.AreEqual(0, server.Requests);
        Assert.IsTrue((await speech.DownloadAsync(cancellationToken: TestContext.CancellationToken)).IsSuccessful);
        Assert.IsTrue(speech.Model.IsDownloaded);
        Assert.IsFalse(speech.Model.IsEnabled);
        await speech.SetEnabledAsync(true, TestContext.CancellationToken);
        await using KokoroSpeech reopened = Create(folder.Path, server);
        Assert.IsTrue(reopened.Model.IsAvailable);
        Assert.IsFalse((await reopened.SpeakAsync("Hello", "../not-a-voice", TestContext.CancellationToken)).IsSuccessful);
        await reopened.SetEnabledAsync(false, TestContext.CancellationToken);
        Assert.IsTrue(reopened.Model.IsDownloaded);
        Assert.IsFalse(reopened.Model.IsAvailable);
        await reopened.RemoveAsync(TestContext.CancellationToken);
        Assert.IsFalse(reopened.Model.IsDownloaded);
        Assert.IsFalse(File.Exists(Path.Combine(folder.Path, "enabled")));
    }

    [TestMethod]
    public async Task Download_CorruptOrFailedDownloadsAreNeverAvailableAndCanBeRetried()
    {
        using TestFolder folder = new();
        using Server server = new(Enumerable.Repeat((byte)'x', MODEL.Length).ToArray());
        await using KokoroSpeech speech = Create(folder.Path, server);
        Assert.IsFalse((await speech.DownloadAsync(cancellationToken: TestContext.CancellationToken)).IsSuccessful);
        Assert.IsFalse(speech.Model.IsDownloaded);
        Assert.IsFalse(File.Exists(Path.Combine(folder.Path, "kokoro.onnx.download")));
        server.Status = HttpStatusCode.NotFound;
        Assert.IsFalse((await speech.DownloadAsync(cancellationToken: TestContext.CancellationToken)).IsSuccessful);
        server.Status = HttpStatusCode.OK;
        server.Content = MODEL;
        Assert.IsTrue((await speech.DownloadAsync(cancellationToken: TestContext.CancellationToken)).IsSuccessful);
        Assert.IsTrue(speech.Model.IsDownloaded);
    }

    [TestMethod]
    public async Task Download_CancellationReleasesTheGateAndDoesNotInstallAPartialModel()
    {
        using TestFolder folder = new();
        using Server server = new(MODEL) { Hold = true };
        await using KokoroSpeech speech = Create(folder.Path, server);
        using CancellationTokenSource canceled = new();
        Task<SpeechResult> downloading = speech.DownloadAsync(cancellationToken: canceled.Token);
        await server.Started.Task.WaitAsync(TestContext.CancellationToken);
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => downloading);
        Assert.IsFalse(speech.Model.IsDownloaded);
        server.Hold = false;
        Assert.IsTrue((await speech.DownloadAsync(cancellationToken: TestContext.CancellationToken)).IsSuccessful);
    }

    [TestMethod]
    public async Task BundledVoicesAndPhonemizerWorkWithTheSharedOnnxRuntime()
    {
        using TestFolder folder = new();
        using Server server = new(MODEL);
        await using KokoroSpeech speech = Create(folder.Path, server);
        using SessionOptions options = new();
        Assert.IsTrue(OrtEnv.Instance().GetAvailableProviders().Contains("CPUExecutionProvider"));
        foreach (SpeechVoice voice in speech.Voices)
        {
            KokoroVoice data = KokoroVoice.FromPath(Path.Combine(AppContext.BaseDirectory, "SpeechVoices", voice.Id + ".npy"));
            Assert.AreEqual(256, data.Features.GetLength(2));
        }

        Assert.IsNotEmpty(Tokenizer.Tokenize("Hello, how are you today?", "en-us"));
        Assert.IsNotEmpty(Tokenizer.Tokenize("Hello, how are you today?", "en-gb"));
    }

    /// <summary>No network or audio device is needed. Set HOMILIA_KOKORO_TEST_MODEL to an official downloaded model.</summary>
    [TestMethod]
    [TestCategory("LocalSpeech")]
    public async Task RealKokoro_SynthesizesBothGendersAndCancelsBeforeTheNextPlayback()
    {
        string? modelPath = Environment.GetEnvironmentVariable("HOMILIA_KOKORO_TEST_MODEL");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            Assert.Inconclusive("Set HOMILIA_KOKORO_TEST_MODEL to a downloaded kokoro.onnx to test real synthesis.");
        }

        await using FileStream input = File.OpenRead(modelPath);
        Assert.AreEqual(KokoroSpeech.MODEL_SHA256, Convert.ToHexString(await SHA256.HashDataAsync(input, TestContext.CancellationToken)).ToLowerInvariant());
        using TestFolder folder = new();
        File.Copy(modelPath, Path.Combine(folder.Path, "kokoro.onnx"));
        using Server server = new(MODEL);
        int playbacks = 0;
        using CancellationTokenSource stop = new();
        await using KokoroSpeech speech = new(folder.Path, server, NullLogger<KokoroSpeech>.Instance, (samples, token) =>
        {
            Assert.IsGreaterThan(2400, samples.Length);
            Assert.Contains(sample => Math.Abs(sample) > 0.001, samples);
            playbacks++;
            if (playbacks == 3)
            {
                stop.Cancel();
            }
            return Task.CompletedTask;
        });
        await speech.SetEnabledAsync(true, TestContext.CancellationToken);
        Assert.IsTrue((await speech.SpeakAsync("Hello, how are you today?", "af_heart", TestContext.CancellationToken)).IsSuccessful);
        Assert.IsTrue((await speech.SpeakAsync("Hello, how are you today?", "bm_george", TestContext.CancellationToken)).IsSuccessful);
        await Assert.ThrowsAsync<OperationCanceledException>(() => speech.SpeakAsync(string.Concat(Enumerable.Repeat("Hello, how are you today? ", 100)), "af_heart", stop.Token));
        Assert.AreEqual(3, playbacks);
        Assert.AreEqual(0, server.Requests);
    }

    private static KokoroSpeech Create(string directory, Server server)
    {
        return new(directory, server, NullLogger<KokoroSpeech>.Instance, (_, _) => Task.CompletedTask, DOWNLOAD);
    }

    private sealed class Server : HttpMessageHandler, IHttpClientFactory
    {
        public byte[] Content { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool Hold { get; set; }
        public int Requests { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Server(byte[] content)
        {
            Content = content;
        }
        public HttpClient CreateClient(string name)
        {
            return new(this, disposeHandler: false);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Started.TrySetResult();
            if (Hold)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return new(Status) { Content = new ByteArrayContent(Content) };
        }
    }

    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HomiliaSpeech-" + Guid.NewGuid().ToString("N"));
        public TestFolder()
        {
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
