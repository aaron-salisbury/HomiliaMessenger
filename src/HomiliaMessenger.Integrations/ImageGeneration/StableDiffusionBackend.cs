using HomiliaMessenger.Business.Modules.ImageGeneration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace HomiliaMessenger.Integrations.ImageGeneration;

/// <summary>
/// Pixels a model drew, row by row, with <see cref="Channels"/> bytes per pixel (RGB or RGBA).
/// </summary>
internal sealed record GeneratedImage(int Width, int Height, int Channels, byte[] Pixels);

/// <summary>
/// What selfies add to an image model: IP-Adapter Plus-Face, and the image encoder that reads a photo's face for it.
/// </summary>
internal sealed record FaceMatchingFiles(string ImageEncoderPath, string AdapterPath);

/// <summary>
/// One image to draw: what to draw, what to avoid, and, with face matching, the face to keep.
/// </summary>
/// <param name="NegativePrompt">What to steer away from; only takes effect with guidance above 1.</param>
/// <param name="Face">The photo whose face the image keeps, as RGB pixels; needs a context opened with face matching.</param>
/// <param name="FaceStrength">How strongly the face is kept, from 0 to 1.</param>
internal sealed record Drawing(string Prompt, long Seed, string? NegativePrompt = null, GeneratedImage? Face = null, float FaceStrength = 0);

/// <summary>
/// Opens image models in a runtime. The seam between image generation's lifecycle (one at a time, cancellation, release)
/// and the native runtime, so the lifecycle can be tested without it.
/// </summary>
internal interface IStableDiffusionBackend
{
    /// <summary>
    /// Why images cannot be generated on this system, such as the runtime not being included in this build, or
    /// <see langword="null"/>. Checking does not load the runtime.
    /// </summary>
    string? RuntimeProblem { get; }

    /// <summary>
    /// Loads a model, with face matching when given. The caller owns the context and must dispose it, which releases the
    /// model's memory.
    /// </summary>
    IStableDiffusionContext Open(string modelPath, FaceMatchingFiles? faceMatching = null);

    /// <summary>
    /// Converts a model file to a GGUF file whose weights are in Q8_0, as the runtime's command-line tool does with
    /// <c>-M convert --type q8_0</c>. Blocks until done.
    /// </summary>
    /// <exception cref="InvalidOperationException">The runtime is unavailable or the conversion failed.</exception>
    void ConvertToQ8(string inputPath, string outputPath);
}

/// <summary>
/// A loaded model, which draws one image at a time.
/// </summary>
internal interface IStableDiffusionContext : IDisposable
{
    /// <summary>
    /// Draws an image, blocking until it is done.
    /// </summary>
    /// <exception cref="OperationCanceledException"><see cref="Cancel"/> was called.</exception>
    GeneratedImage Generate(Drawing drawing, ImageGenerationSettings settings, Action<ImageGenerationStage, long, long> progress);

    /// <summary>
    /// Asks a generation in progress, or the next one, to stop. Safe to call from any thread.
    /// </summary>
    void Cancel();
}

/// <summary>
/// The stable-diffusion.cpp runtime included with the app, loaded on first use.
/// </summary>
internal sealed class NativeStableDiffusionBackend : IStableDiffusionBackend
{
    private readonly string? _directory;
    private readonly ILogger<NativeStableDiffusionBackend> _logger;
    private readonly Lock _loadLock = new();
    private StableDiffusionNative? _native;
    private string? _loadProblem;

    // Kept for the life of the process: the runtime holds on to it.
    private StableDiffusionNative.LogCallback? _logCallback;

    public NativeStableDiffusionBackend(ILogger<NativeStableDiffusionBackend> logger) : this(AppContext.BaseDirectory, logger)
    {
    }

    internal NativeStableDiffusionBackend(string baseDirectory, ILogger<NativeStableDiffusionBackend> logger)
    {
        _directory = StableDiffusionNative.RuntimeDirectory(baseDirectory);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string? RuntimeProblem
    {
        get
        {
            if (_directory is null)
            {
                return "Image generation is available on 64-bit Windows and Linux only.";
            }

            if (!StableDiffusionNative.LibraryFiles().All(file => File.Exists(Path.Combine(_directory, file))))
            {
                return "This build of Homilia Messenger does not include image generation for this system.";
            }

            return _loadProblem;
        }
    }

    public IStableDiffusionContext Open(string modelPath, FaceMatchingFiles? faceMatching = null)
    {
        return new NativeStableDiffusionContext(Load(), modelPath, faceMatching);
    }

    public void ConvertToQ8(string inputPath, string outputPath)
    {
        StableDiffusionNative native = Load();
        IntPtr input = Marshal.StringToCoTaskMemUTF8(inputPath);
        IntPtr output = Marshal.StringToCoTaskMemUTF8(outputPath);

        // Unused paths and the type rules are empty strings, never null, as the runtime's command-line tool passes them:
        // the converter reads each as a string.
        IntPtr empty = Marshal.StringToCoTaskMemUTF8(string.Empty);

        try
        {
            NativeStableDiffusionContext.LastError = null;

            if (!native.Convert(input, empty, empty, empty, empty, empty, output, StableDiffusionNative.SD_TYPE_Q8_0, empty, false, native.PhysicalCores(), IntPtr.Zero, 0))
            {
                string? problem = NativeStableDiffusionContext.LastError;
                throw new InvalidOperationException("The file could not be converted" + (problem is null ? "." : ": " + problem));
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(empty);
            Marshal.FreeCoTaskMem(output);
            Marshal.FreeCoTaskMem(input);
        }
    }

    private StableDiffusionNative Load()
    {
        lock (_loadLock)
        {
            if (_native is not null)
            {
                return _native;
            }

            if (RuntimeProblem is { } problem)
            {
                throw new InvalidOperationException(problem);
            }

            try
            {
                StableDiffusionNative native = StableDiffusionNative.Load(_directory!);
                _logCallback = Log;
                native.SetLogCallback(_logCallback, IntPtr.Zero);
                _native = native;
                _logger.LogInformation("Loaded the image generation runtime from {Directory}.", _directory);

                return native;
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
            {
                _logger.LogError(error, "Could not load the image generation runtime from {Directory}.", _directory);
                _loadProblem = "Image generation could not start on this computer: " + error.Message;
                throw new InvalidOperationException(_loadProblem, error);
            }
        }
    }

    /// <summary>
    /// Records the runtime's messages in the app's log: warnings and errors as such, progress only when debugging.
    /// </summary>
    private void Log(int level, IntPtr text, IntPtr data)
    {
        try
        {
            string message = (Marshal.PtrToStringUTF8(text) ?? string.Empty).Trim();

            if (message.Length == 0)
            {
                return;
            }

            switch (level)
            {
                case >= 4:
                    _logger.LogError("stable-diffusion.cpp: {Message}", message);
                    NativeStableDiffusionContext.LastError = message;
                    break;
                case 3:
                    _logger.LogWarning("stable-diffusion.cpp: {Message}", message);
                    break;
                case 2:
                    _logger.LogDebug("stable-diffusion.cpp: {Message}", message);
                    break;
            }
        }
        catch (Exception)
        {
            // A callback from native code must never throw.
        }
    }
}

/// <summary>
/// A model loaded in stable-diffusion.cpp. Its native context is released when disposed, or by its safe handle if it
/// never is.
/// </summary>
internal sealed class NativeStableDiffusionContext : IStableDiffusionContext
{
    // One delegate for every context, kept alive for as long as the runtime may call it.
    private static readonly StableDiffusionNative.GenerationProgressCallback PROGRESS_CALLBACK = OnProgress;

    private readonly StableDiffusionNative _native;
    private readonly ContextHandle _handle;
    private readonly Lock _lock = new();
    private GCHandle _self;
    private Action<ImageGenerationStage, long, long>? _progress;
    private volatile bool _cancelRequested;
    private bool _disposed;

    /// <summary>
    /// The last error the runtime reported, which explains a failed generation better than its result alone.
    /// </summary>
    internal static string? LastError { get; set; }

    /// <exception cref="InvalidOperationException">The model could not be loaded.</exception>
    internal NativeStableDiffusionContext(StableDiffusionNative native, string modelPath, FaceMatchingFiles? faceMatching = null)
    {
        _native = native;
        IntPtr parameters = Marshal.AllocHGlobal(StableDiffusionNative.PARAMS_BUFFER_BYTES);
        IntPtr path = Marshal.StringToCoTaskMemUTF8(modelPath);
        IntPtr backend = Marshal.StringToCoTaskMemUTF8("cpu");
        IntPtr imageEncoder = faceMatching is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(faceMatching.ImageEncoderPath);
        IntPtr adapter = faceMatching is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(faceMatching.AdapterPath);

        try
        {
            StableDiffusionNative.Clear(parameters);
            native.ContextParamsInit(parameters);
            Marshal.WriteIntPtr(parameters, StableDiffusionNative.ContextOffsets.MODEL_PATH, path);

            if (faceMatching is not null)
            {
                Marshal.WriteIntPtr(parameters, StableDiffusionNative.ContextOffsets.CLIP_VISION, imageEncoder);
                Marshal.WriteIntPtr(parameters, StableDiffusionNative.ContextOffsets.IP_ADAPTER, adapter);
                HasFaceMatching = true;
            }

            // CPU only for now; a GPU backend would be chosen here.
            Marshal.WriteIntPtr(parameters, StableDiffusionNative.ContextOffsets.BACKEND, backend);
            LastError = null;
            IntPtr context = native.NewContext(parameters);

            if (context == IntPtr.Zero)
            {
                throw new InvalidOperationException("The image model could not be loaded" + (LastError is null ? "." : ": " + LastError));
            }

            _handle = new ContextHandle(context, native.FreeContext);
        }
        finally
        {
            Marshal.FreeCoTaskMem(adapter);
            Marshal.FreeCoTaskMem(imageEncoder);
            Marshal.FreeCoTaskMem(backend);
            Marshal.FreeCoTaskMem(path);
            Marshal.FreeHGlobal(parameters);
        }

        if (!native.SupportsImageGeneration(_handle.DangerousGetHandle()))
        {
            _handle.Dispose();
            throw new InvalidOperationException("This model cannot generate images.");
        }

        _self = GCHandle.Alloc(this);
        native.SetGenerationProgressCallback(_handle.DangerousGetHandle(), PROGRESS_CALLBACK, GCHandle.ToIntPtr(_self));
    }

    /// <summary>
    /// Whether the model was opened with face matching, so a drawing can keep a face.
    /// </summary>
    internal bool HasFaceMatching { get; }

    public GeneratedImage Generate(Drawing drawing, ImageGenerationSettings settings, Action<ImageGenerationStage, long, long> progress)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (drawing.Face is { } face && (!HasFaceMatching || face.Channels != 3 || face.Pixels.Length != face.Width * face.Height * 3))
        {
            throw new InvalidOperationException(HasFaceMatching ? "The face to keep must be RGB pixels." : "The model was opened without face matching.");
        }

        IntPtr context = _handle.DangerousGetHandle();
        int sampler = WithString(settings.Sampler, name => _native.SampleMethodFromName(name));

        if (sampler == StableDiffusionNative.SAMPLE_METHOD_COUNT)
        {
            throw new InvalidOperationException($"The runtime has no sampler named \"{settings.Sampler}\".");
        }

        int scheduler = settings.Scheduler is null
            ? _native.DefaultScheduler(context, sampler)
            : WithString(settings.Scheduler, name => _native.SchedulerFromName(name));

        if (scheduler == StableDiffusionNative.SCHEDULER_COUNT)
        {
            throw new InvalidOperationException($"The runtime has no scheduler named \"{settings.Scheduler}\".");
        }

        IntPtr parameters = Marshal.AllocHGlobal(StableDiffusionNative.PARAMS_BUFFER_BYTES);
        IntPtr promptText = Marshal.StringToCoTaskMemUTF8(drawing.Prompt);
        IntPtr negativeText = drawing.NegativePrompt is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(drawing.NegativePrompt);
        IntPtr facePixels = IntPtr.Zero;
        IntPtr result = IntPtr.Zero;
        _progress = progress;

        try
        {
            StableDiffusionNative.Clear(parameters);
            _native.ImageParamsInit(parameters);
            Marshal.WriteIntPtr(parameters, StableDiffusionNative.ImageOffsets.PROMPT, promptText);
            Marshal.WriteInt32(parameters, StableDiffusionNative.ImageOffsets.WIDTH, settings.Width);
            Marshal.WriteInt32(parameters, StableDiffusionNative.ImageOffsets.HEIGHT, settings.Height);
            StableDiffusionNative.WriteSingle(parameters, StableDiffusionNative.ImageOffsets.TEXT_CFG, settings.GuidanceScale);
            Marshal.WriteInt32(parameters, StableDiffusionNative.ImageOffsets.SAMPLE_METHOD, sampler);
            Marshal.WriteInt32(parameters, StableDiffusionNative.ImageOffsets.SCHEDULER, scheduler);
            Marshal.WriteInt32(parameters, StableDiffusionNative.ImageOffsets.SAMPLE_STEPS, settings.Steps);
            Marshal.WriteInt64(parameters, StableDiffusionNative.ImageOffsets.SEED, drawing.Seed);

            if (negativeText != IntPtr.Zero)
            {
                Marshal.WriteIntPtr(parameters, StableDiffusionNative.ImageOffsets.NEGATIVE_PROMPT, negativeText);
            }

            if (drawing.Face is { } reference)
            {
                facePixels = Marshal.AllocHGlobal(reference.Pixels.Length);
                Marshal.Copy(reference.Pixels, 0, facePixels, reference.Pixels.Length);
                Marshal.WriteInt32(parameters, StableDiffusionNative.ImageOffsets.IP_ADAPTER_IMAGE_WIDTH, reference.Width);
                Marshal.WriteInt32(parameters, StableDiffusionNative.ImageOffsets.IP_ADAPTER_IMAGE_HEIGHT, reference.Height);
                Marshal.WriteInt32(parameters, StableDiffusionNative.ImageOffsets.IP_ADAPTER_IMAGE_CHANNELS, 3);
                Marshal.WriteIntPtr(parameters, StableDiffusionNative.ImageOffsets.IP_ADAPTER_IMAGE_DATA, facePixels);
                StableDiffusionNative.WriteSingle(parameters, StableDiffusionNative.ImageOffsets.IP_ADAPTER_STRENGTH, drawing.FaceStrength);
            }

            // The runtime clears any earlier cancellation when it starts, so one asked for before then is checked here,
            // and asked again from the progress callback once it has started.
            if (_cancelRequested)
            {
                throw new OperationCanceledException();
            }

            LastError = null;
            bool generated = _native.GenerateImage(context, parameters, out result);

            if (_cancelRequested)
            {
                throw new OperationCanceledException();
            }

            if (!generated || result == IntPtr.Zero)
            {
                throw new InvalidOperationException("The image could not be generated" + (LastError is null ? "." : ": " + LastError));
            }

            return ReadImage(result, settings);
        }
        finally
        {
            _progress = null;

            if (result != IntPtr.Zero)
            {
                _native.FreeImageResult(result);
            }

            if (facePixels != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(facePixels);
            }

            Marshal.FreeCoTaskMem(negativeText);
            Marshal.FreeCoTaskMem(promptText);
            Marshal.FreeHGlobal(parameters);

        }
    }

    public void Cancel()
    {
        _cancelRequested = true;

        lock (_lock)
        {
            if (!_disposed)
            {
                _native.Cancel(_handle.DangerousGetHandle(), StableDiffusionNative.SD_CANCEL_ALL);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _handle.Dispose();

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private static GeneratedImage ReadImage(IntPtr result, ImageGenerationSettings settings)
    {
        IntPtr item = Marshal.ReadIntPtr(result, StableDiffusionNative.ResultOffsets.ITEMS);
        int count = Marshal.ReadInt32(result, StableDiffusionNative.ResultOffsets.COUNT);

        if (item == IntPtr.Zero || count < 1)
        {
            throw new InvalidOperationException("The runtime returned no image.");
        }

        int width = Marshal.ReadInt32(item, StableDiffusionNative.ResultOffsets.WIDTH);
        int height = Marshal.ReadInt32(item, StableDiffusionNative.ResultOffsets.HEIGHT);
        int channels = Marshal.ReadInt32(item, StableDiffusionNative.ResultOffsets.CHANNELS);
        IntPtr data = Marshal.ReadIntPtr(item, StableDiffusionNative.ResultOffsets.DATA);

        if (width != settings.Width || height != settings.Height || channels is not (3 or 4) || data == IntPtr.Zero)
        {
            throw new InvalidOperationException($"The runtime returned an unexpected {width}×{height} image with {channels} channels.");
        }

        byte[] pixels = new byte[checked(width * height * channels)];
        Marshal.Copy(data, pixels, 0, pixels.Length);

        return new GeneratedImage(width, height, channels, pixels);
    }

    private static T WithString<T>(string value, Func<IntPtr, T> call)
    {
        IntPtr text = Marshal.StringToCoTaskMemUTF8(value);

        try
        {
            return call(text);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    /// <summary>
    /// Reports the runtime's progress, and repeats a cancellation the runtime may have cleared when it started.
    /// </summary>
    private static void OnProgress(IntPtr progress, IntPtr data)
    {
        try
        {
            if (data == IntPtr.Zero || GCHandle.FromIntPtr(data).Target is not NativeStableDiffusionContext context)
            {
                return;
            }

            if (context._cancelRequested)
            {
                context._native.Cancel(context._handle.DangerousGetHandle(), StableDiffusionNative.SD_CANCEL_ALL);
            }

            StableDiffusionNative.GenerationProgress update = Marshal.PtrToStructure<StableDiffusionNative.GenerationProgress>(progress);
            ImageGenerationStage? stage = Marshal.PtrToStringUTF8(update.Stage) switch
            {
                "preparing" or "conditioning" => ImageGenerationStage.Preparing,
                "sampling" => ImageGenerationStage.Sampling,
                "vae_decode" => ImageGenerationStage.Decoding,
                _ => null
            };

            if (stage is { } known)
            {
                context._progress?.Invoke(known, update.Completed, update.Total);
            }
        }
        catch (Exception)
        {
            // A callback from native code must never throw.
        }
    }

    private sealed class ContextHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly StableDiffusionNative.FreeFunction _free;

        public ContextHandle(IntPtr context, StableDiffusionNative.FreeFunction free) : base(ownsHandle: true)
        {
            _free = free;
            SetHandle(context);
        }

        protected override bool ReleaseHandle()
        {
            _free(handle);
            return true;
        }
    }
}
