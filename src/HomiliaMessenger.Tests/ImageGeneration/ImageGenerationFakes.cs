using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Integrations.ImageGeneration;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.ImageGeneration;

/// <summary>
/// Records progress as it is reported, on the reporting thread, unlike <see cref="Progress{T}"/>.
/// </summary>
internal sealed class SynchronousProgress : IProgress<ImageGenerationProgress>
{
    private readonly List<ImageGenerationProgress> _updates;
    private readonly Action<ImageGenerationProgress>? _onReport;

    public SynchronousProgress(List<ImageGenerationProgress> updates, Action<ImageGenerationProgress>? onReport = null)
    {
        _updates = updates;
        _onReport = onReport;
    }

    public void Report(ImageGenerationProgress value)
    {
        lock (_updates)
        {
            _updates.Add(value);
        }

        _onReport?.Invoke(value);
    }
}

internal sealed class NoNetwork : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        throw new InvalidOperationException("This test must not use the network.");
    }
}

/// <summary>
/// Reads the PNG files <see cref="PngEncoder"/> writes: 8-bit RGB or RGBA with unfiltered rows.
/// </summary>
internal static class PngTestDecoder
{
    internal static (int Width, int Height, byte[] Pixels) Decode(byte[] png)
    {
        if (png.Length < 8 || png[0] != 137 || png[1] != 80 || png[2] != 78 || png[3] != 71)
        {
            throw new InvalidDataException("Not a PNG file.");
        }

        int width = 0;
        int height = 0;
        int channels = 0;
        using MemoryStream compressed = new();

        for (int offset = 8; offset < png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            ReadOnlySpan<byte> data = png.AsSpan(offset + 8, length);

            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                channels = data[9] == 2 ? 3 : 4;
            }
            else if (type == "IDAT")
            {
                compressed.Write(data);
            }

            offset += 12 + length;
        }

        compressed.Position = 0;
        using ZLibStream zlib = new(compressed, CompressionMode.Decompress);
        using MemoryStream rows = new();
        zlib.CopyTo(rows);
        byte[] filtered = rows.ToArray();
        int stride = width * channels;
        byte[] pixels = new byte[stride * height];

        for (int row = 0; row < height; row++)
        {
            if (filtered[row * (stride + 1)] != 0)
            {
                throw new InvalidDataException("Only unfiltered rows are expected.");
            }

            Array.Copy(filtered, (row * (stride + 1)) + 1, pixels, row * stride, stride);
        }

        return (width, height, pixels);
    }
}

/// <summary>
/// A runtime that draws a solid image after reporting each stage, counting the contexts it opens and releases.
/// </summary>
internal sealed class FakeBackend : IStableDiffusionBackend
{
    private int _opened;
    private int _disposed;

    public string? RuntimeProblem { get; set; }

    /// <summary>
    /// Thrown when a model is opened, as when its file cannot be loaded.
    /// </summary>
    public Exception? OpenFailure { get; set; }

    /// <summary>
    /// Thrown from <see cref="IStableDiffusionContext.Generate"/>, as when the runtime fails while drawing.
    /// </summary>
    public Exception? GenerateFailure { get; set; }

    /// <summary>
    /// Holds the drawing at the first sampling step until set, so a test can act while an image is being drawn.
    /// </summary>
    public ManualResetEventSlim? Hold { get; set; }

    public ManualResetEventSlim Drawing { get; } = new();

    public int Opened => _opened;

    public int Disposed => _disposed;

    public List<(string Prompt, ImageGenerationSettings Settings, long Seed)> Requests { get; } = [];

    /// <summary>
    /// Every drawing asked for, with what face matching it was opened with.
    /// </summary>
    public List<(Drawing Drawing, FaceMatchingFiles? FaceMatching)> Drawings { get; } = [];

    /// <summary>
    /// Conversions asked for, as input and output paths. Each writes an output of <see cref="ConvertedBytes"/> bytes.
    /// </summary>
    public List<(string Input, string Output)> Conversions { get; } = [];

    public long ConvertedBytes { get; set; } = FaceMatchingCatalog.PREPARED_ENCODER_BYTES;

    public IStableDiffusionContext Open(string modelPath, FaceMatchingFiles? faceMatching = null)
    {
        if (OpenFailure is not null)
        {
            throw OpenFailure;
        }

        Interlocked.Increment(ref _opened);
        return new Context(this, faceMatching);
    }

    /// <summary>
    /// Called while a conversion runs, as when the user pauses meanwhile.
    /// </summary>
    public Action? Converting { get; set; }

    public void ConvertToQ8(string inputPath, string outputPath)
    {
        lock (Conversions)
        {
            Conversions.Add((inputPath, outputPath));
        }

        using FileStream output = File.Create(outputPath);
        output.SetLength(ConvertedBytes);
        Converting?.Invoke();
    }

    private sealed class Context : IStableDiffusionContext
    {
        private readonly FakeBackend _backend;
        private readonly FaceMatchingFiles? _faceMatching;
        private volatile bool _canceled;
        private bool _isDisposed;

        public Context(FakeBackend backend, FaceMatchingFiles? faceMatching)
        {
            _backend = backend;
            _faceMatching = faceMatching;
        }

        public GeneratedImage Generate(Drawing drawing, ImageGenerationSettings settings, Action<ImageGenerationStage, long, long> progress)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            long seed = drawing.Seed;

            lock (_backend.Requests)
            {
                _backend.Requests.Add((drawing.Prompt, settings, seed));
                _backend.Drawings.Add((drawing, _faceMatching));
            }


            progress(ImageGenerationStage.Preparing, 0, 0);

            for (int step = 0; step <= settings.Steps; step++)
            {
                progress(ImageGenerationStage.Sampling, step, settings.Steps);

                if (step == 0)
                {
                    _backend.Drawing.Set();

                    // Released by the test, or by a cancellation (checked as the runtime does, between steps).
                    while (_backend.Hold is { IsSet: false } && !_canceled)
                    {
                        Thread.Sleep(5);
                    }
                }

                if (_canceled)
                {
                    throw new OperationCanceledException();
                }
            }

            if (_backend.GenerateFailure is not null)
            {
                throw _backend.GenerateFailure;
            }

            progress(ImageGenerationStage.Decoding, 0, 0);
            byte[] pixels = new byte[settings.Width * settings.Height * 3];
            Array.Fill(pixels, (byte)(seed % 256));

            return new GeneratedImage(settings.Width, settings.Height, 3, pixels);
        }

        public void Cancel()
        {
            _canceled = true;
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                Interlocked.Increment(ref _backend._disposed);
            }
        }
    }
}

/// <summary>
/// An image generator that answers as told, for testing the portrait service and dialog.
/// </summary>
internal sealed class FakeImageGenerator : IImageGenerator
{
    public ImageGeneratorAvailability Availability { get; set; } = new(true, "Test model", true, null);

    public List<ImageGenerationRequest> Requests { get; } = [];

    /// <summary>
    /// Decides the result of each request; by default, a tiny PNG.
    /// </summary>
    public Func<ImageGenerationRequest, CancellationToken, Task<ImageGenerationResult>> Respond { get; set; } = (request, _) =>
        Task.FromResult(ImageGenerationResult.Success(TinyPng((byte)(request.Seed % 256))));

    public ImageGeneratorAvailability GetAvailability()
    {
        return Availability;
    }

    public Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request, IProgress<ImageGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        progress?.Report(new ImageGenerationProgress(ImageGenerationStage.Sampling, 1, 4));

        return Respond(request, cancellationToken);
    }

    /// <summary>
    /// A valid 2×2 PNG of one gray value.
    /// </summary>
    internal static byte[] TinyPng(byte value)
    {
        return PngEncoder.Encode(new GeneratedImage(2, 2, 3, [value, value, value, value, value, value, value, value, value, value, value, value]));
    }
}
