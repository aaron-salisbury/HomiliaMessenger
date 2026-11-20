using HomiliaMessenger.Business.Modules.ImageGeneration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
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
/// What selfies add to a Stable Diffusion model: IP-Adapter Plus-Face, and the image encoder that reads a photo's face for
/// it.
/// </summary>
internal sealed record FaceMatchingFiles(string ImageEncoderPath, string AdapterPath);

/// <summary>
/// The files a model is opened from: a self-contained checkpoint, or a diffusion model with the text encoder and VAE it
/// is read and decoded with, as FLUX.2 [klein] is published.
/// </summary>
/// <param name="Checkpoint">A checkpoint with its text encoder and VAE inside, such as DreamShaper's.</param>
/// <param name="DiffusionModel">A diffusion model alone.</param>
/// <param name="TextEncoder">A language model that reads the prompt for the diffusion model, such as Qwen3-4B.</param>
/// <param name="Vae">The VAE that turns the drawing into pixels, when not inside the checkpoint.</param>
internal sealed record ImageModelPaths(string? Checkpoint, string? DiffusionModel = null, string? TextEncoder = null, string? Vae = null)
{
    public IEnumerable<string> All => new[] { Checkpoint, DiffusionModel, TextEncoder, Vae }.OfType<string>();
}

/// <summary>
/// What to open a model with, and where it runs.
/// </summary>
/// <param name="Device">
/// The runtime's name for the device to draw on: <see cref="CPU"/>, or a graphics card such as <c>CUDA0</c> or
/// <c>Vulkan0</c>.
/// </param>
/// <param name="FaceMatching">Face matching's files, for selfies of models that keep a face with it.</param>
internal sealed record ImageModelLoad(ImageModelPaths Paths, string Device, FaceMatchingFiles? FaceMatching = null)
{
    internal const string CPU = "CPU";

    public bool IsGpu => !string.Equals(Device, CPU, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One image to draw: what to draw, what to avoid, and, with face matching or a reference image, the face to keep.
/// </summary>
/// <param name="NegativePrompt">What to steer away from; only takes effect with guidance above 1.</param>
/// <param name="Face">The photo whose face the image keeps, as RGB pixels; needs a context opened with face matching.</param>
/// <param name="FaceStrength">How strongly the face is kept, from 0 to 1.</param>
/// <param name="References">Photos the model draws from directly, as FLUX.2 [klein] keeps a face, as RGB pixels.</param>
/// <param name="Loras">LoRAs applied while drawing, by path and strength.</param>
internal sealed record Drawing(
    string Prompt,
    long Seed,
    string? NegativePrompt = null,
    GeneratedImage? Face = null,
    float FaceStrength = 0,
    IReadOnlyList<GeneratedImage>? References = null,
    IReadOnlyList<ImageLoraUse>? Loras = null);

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
    /// The graphics cards the runtime can draw on, the preferred first: an NVIDIA card through CUDA when the CUDA runtime
    /// is in use, then cards with their own memory, then integrated ones. Loads the runtime; empty when it cannot load.
    /// </summary>
    IReadOnlyList<ImageGpuDevice> GpuDevices();

    /// <summary>
    /// Whether the runtime in use is the downloaded CUDA one; <see langword="false"/> until the runtime has loaded.
    /// </summary>
    bool IsCudaActive { get; }

    /// <summary>
    /// Whether the runtime has been loaded in this process, after which another cannot replace it until the app restarts.
    /// </summary>
    bool IsLoaded { get; }

    /// <summary>
    /// Loads a model on a device, with face matching when given. The caller owns the context and must dispose it, which
    /// releases the model's memory.
    /// </summary>
    IStableDiffusionContext Open(ImageModelLoad load);

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
/// The stable-diffusion.cpp runtime, loaded on first use: the CUDA runtime when it was downloaded in Settings and this
/// computer has an NVIDIA driver, otherwise the one included with the app (its CPU and Vulkan backends). Only one can be
/// loaded in a process, so a CUDA runtime downloaded after the first image is used from the next start.
/// </summary>
internal sealed class NativeStableDiffusionBackend : IStableDiffusionBackend
{
    private readonly string? _directory;
    private readonly string? _cudaDirectory;
    private readonly ILogger<NativeStableDiffusionBackend> _logger;
    private readonly Lock _loadLock = new();
    private StableDiffusionNative? _native;
    private string? _loadProblem;
    private IReadOnlyList<ImageGpuDevice>? _gpuDevices;

    // Kept for the life of the process: the runtime holds on to it.
    private StableDiffusionNative.LogCallback? _logCallback;

    public NativeStableDiffusionBackend(ILogger<NativeStableDiffusionBackend> logger) : this(AppContext.BaseDirectory, logger)
    {
    }

    /// <param name="cudaDirectory">Where the CUDA runtime is unpacked when downloaded (see <see cref="CudaRuntime"/>).</param>
    internal NativeStableDiffusionBackend(string baseDirectory, ILogger<NativeStableDiffusionBackend> logger, string? cudaDirectory = null)
    {
        _directory = StableDiffusionNative.RuntimeDirectory(baseDirectory);
        _cudaDirectory = cudaDirectory;
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

    public bool IsLoaded
    {
        get
        {
            lock (_loadLock)
            {
                return _native is not null;
            }
        }
    }

    public bool IsCudaActive
    {
        get
        {
            lock (_loadLock)
            {
                return _native?.IsCuda == true;
            }
        }
    }

    public IStableDiffusionContext Open(ImageModelLoad load)
    {
        ArgumentNullException.ThrowIfNull(load);
        return new NativeStableDiffusionContext(Load(), load);
    }

    public IReadOnlyList<ImageGpuDevice> GpuDevices()
    {
        if (RuntimeProblem is not null)
        {
            return [];
        }

        StableDiffusionNative native;

        try
        {
            native = Load();
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        lock (_loadLock)
        {
            if (_gpuDevices is not null)
            {
                return _gpuDevices;
            }

            List<(ImageGpuDevice Device, int Type)> found = [];

            for (nuint index = 0, count = native.DeviceCount(); index < count; index++)
            {
                IntPtr device = native.DeviceAt(index);
                int type = native.DeviceType(device);

                if (type is not (StableDiffusionNative.DEVICE_TYPE_GPU or StableDiffusionNative.DEVICE_TYPE_IGPU))
                {
                    continue;
                }

                native.DeviceMemory(device, out _, out nuint total);
                string name = Marshal.PtrToStringUTF8(native.DeviceName(device)) ?? $"GPU{index}";
                string description = Marshal.PtrToStringUTF8(native.DeviceDescription(device)) ?? name;
                found.Add((new ImageGpuDevice(name, description, (long)total, type == StableDiffusionNative.DEVICE_TYPE_IGPU), type));
            }

            // CUDA before Vulkan for the same card: several times faster at this commit, above all with reference images.
            _gpuDevices = [.. found
                .OrderBy(entry => entry.Device.IsCuda ? 0 : 1)
                .ThenBy(entry => entry.Type == StableDiffusionNative.DEVICE_TYPE_GPU ? 0 : 1)
                .Select(entry => entry.Device)];

            foreach (ImageGpuDevice device in _gpuDevices)
            {
                _logger.LogInformation("Image generation can use {Device} ({Description}, {Memory:0.0} GB).", device.Name, device.Description, device.TotalBytes / 1e9);
            }

            return _gpuDevices;
        }
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

            if (LoadCuda() is { } cuda)
            {
                return cuda;
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
    /// Loads the downloaded CUDA runtime, when it is complete and this computer has an NVIDIA driver; otherwise, or when it
    /// fails to load, the included runtime is used instead.
    /// </summary>
    private StableDiffusionNative? LoadCuda()
    {
        if (_cudaDirectory is null || !OperatingSystem.IsWindows() || !CudaRuntime.IsComplete(_cudaDirectory))
        {
            return null;
        }

        if (!NativeLibrary.TryLoad("nvcuda.dll", out IntPtr driver))
        {
            _logger.LogInformation("The CUDA runtime is downloaded, but this computer has no NVIDIA driver; using the included runtime.");
            return null;
        }

        // Asked of the driver before the runtime loads, since a loaded runtime cannot be swapped for the included one.
        if (CudaDeviceCount(driver) <= 0)
        {
            _logger.LogInformation("The CUDA runtime is downloaded, but the NVIDIA driver reports no card it can use; using the included runtime.");
            return null;
        }

        try
        {
            StableDiffusionNative native = StableDiffusionNative.Load(_cudaDirectory, isCuda: true);
            _logCallback = Log;
            native.SetLogCallback(_logCallback, IntPtr.Zero);
            _native = native;
            _logger.LogInformation("Loaded the CUDA image generation runtime from {Directory}.", _cudaDirectory);

            // A loaded runtime cannot be swapped for another in this process; one whose CUDA backend found no card is
            // unstamped, so the next start uses the included runtime and Settings offers the download again.
            if (!HasCudaDevice(native))
            {
                _logger.LogError("The CUDA image generation runtime found no NVIDIA card it can use; the included runtime is used from the next start.");
                File.Delete(Path.Combine(_cudaDirectory, CudaRuntime.STAMP));
            }

            return native;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            _logger.LogError(error, "Could not load the CUDA image generation runtime from {Directory}; using the included runtime.", _cudaDirectory);
            return null;
        }
    }

    /// <summary>
    /// The number of cards the NVIDIA driver can run CUDA on (<c>cuInit</c>, then <c>cuDeviceGetCount</c>); zero when it
    /// cannot start.
    /// </summary>
    private static int CudaDeviceCount(IntPtr driver)
    {
        if (!NativeLibrary.TryGetExport(driver, "cuInit", out IntPtr init) || !NativeLibrary.TryGetExport(driver, "cuDeviceGetCount", out IntPtr getCount))
        {
            return 0;
        }

        return Marshal.GetDelegateForFunctionPointer<CudaInit>(init)(0) == 0 && Marshal.GetDelegateForFunctionPointer<CudaDeviceGetCount>(getCount)(out int count) == 0 ? count : 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CudaInit(uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CudaDeviceGetCount(out int count);

    private static bool HasCudaDevice(StableDiffusionNative native)
    {
        for (nuint index = 0, count = native.DeviceCount(); index < count; index++)
        {
            IntPtr device = native.DeviceAt(index);

            if (native.DeviceType(device) is StableDiffusionNative.DEVICE_TYPE_GPU or StableDiffusionNative.DEVICE_TYPE_IGPU
                && (Marshal.PtrToStringUTF8(native.DeviceName(device)) ?? string.Empty).StartsWith("CUDA", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
    internal NativeStableDiffusionContext(StableDiffusionNative native, ImageModelLoad load)
    {
        _native = native;
        IntPtr parameters = Marshal.AllocHGlobal(StableDiffusionNative.PARAMS_BUFFER_BYTES);
        List<IntPtr> strings = [];

        IntPtr Text(string? value)
        {
            if (value is null)
            {
                return IntPtr.Zero;
            }

            IntPtr text = Marshal.StringToCoTaskMemUTF8(value);
            strings.Add(text);
            return text;
        }

        try
        {
            StableDiffusionNative.Clear(parameters);
            native.ContextParamsInit(parameters);
            Marshal.WriteIntPtr(parameters, StableDiffusionNative.ContextOffsets.MODEL_PATH, Text(load.Paths.Checkpoint));
            Marshal.WriteIntPtr(parameters, StableDiffusionNative.ContextOffsets.DIFFUSION_MODEL, Text(load.Paths.DiffusionModel));
            Marshal.WriteIntPtr(parameters, StableDiffusionNative.ContextOffsets.LLM, Text(load.Paths.TextEncoder));
            Marshal.WriteIntPtr(parameters, StableDiffusionNative.ContextOffsets.VAE, Text(load.Paths.Vae));

            if (load.FaceMatching is { } faceMatching)
            {
                Marshal.WriteIntPtr(parameters, StableDiffusionNative.ContextOffsets.CLIP_VISION, Text(faceMatching.ImageEncoderPath));
                Marshal.WriteIntPtr(parameters, StableDiffusionNative.ContextOffsets.IP_ADAPTER, Text(faceMatching.AdapterPath));
                HasFaceMatching = true;
            }

            Marshal.WriteIntPtr(parameters, StableDiffusionNative.ContextOffsets.BACKEND, Text(load.Device));

            // On a graphics card, flash attention where the runtime's probe finds it faster, as its command-line tool does
            // by default; the C API's default leaves it off. The processor keeps the defaults DreamShaper 8 LCM was verified
            // with, pixel for pixel.
            if (load.IsGpu)
            {
                Marshal.WriteIntPtr(parameters, StableDiffusionNative.ContextOffsets.FLASH_ATTN_MODE, Text("auto"));
            }

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
            foreach (IntPtr text in strings)
            {
                Marshal.FreeCoTaskMem(text);
            }

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

        if (drawing.References?.Any(photo => photo.Channels != 3 || photo.Pixels.Length != photo.Width * photo.Height * 3) == true)
        {
            throw new InvalidOperationException("A reference image must be RGB pixels.");
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
        IntPtr loras = IntPtr.Zero;
        IntPtr references = IntPtr.Zero;
        List<IntPtr> allocations = [];
        List<IntPtr> strings = [];
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

            if (drawing.Loras is { Count: > 0 } used)
            {
                int size = StableDiffusionNative.LoraLayout.SIZE;
                loras = Marshal.AllocHGlobal(size * used.Count);

                for (int index = 0; index < used.Count; index++)
                {
                    IntPtr item = loras + (index * size);

                    for (int offset = 0; offset < size; offset += 4)
                    {
                        Marshal.WriteInt32(item, offset, 0);
                    }

                    IntPtr path = Marshal.StringToCoTaskMemUTF8(used[index].Path);
                    strings.Add(path);
                    StableDiffusionNative.WriteSingle(item, StableDiffusionNative.LoraLayout.MULTIPLIER, used[index].Strength);
                    Marshal.WriteIntPtr(item, StableDiffusionNative.LoraLayout.PATH, path);
                }

                Marshal.WriteIntPtr(parameters, StableDiffusionNative.ImageOffsets.LORAS, loras);
                Marshal.WriteInt32(parameters, StableDiffusionNative.ImageOffsets.LORA_COUNT, used.Count);
            }

            if (drawing.References is { Count: > 0 } photos)
            {
                int size = StableDiffusionNative.ImageLayout.SIZE;
                references = Marshal.AllocHGlobal(size * photos.Count);

                for (int index = 0; index < photos.Count; index++)
                {
                    GeneratedImage photo = photos[index];
                    IntPtr pixels = Marshal.AllocHGlobal(photo.Pixels.Length);
                    allocations.Add(pixels);
                    Marshal.Copy(photo.Pixels, 0, pixels, photo.Pixels.Length);
                    IntPtr item = references + (index * size);
                    Marshal.WriteInt32(item, StableDiffusionNative.ImageLayout.WIDTH, photo.Width);
                    Marshal.WriteInt32(item, StableDiffusionNative.ImageLayout.HEIGHT, photo.Height);
                    Marshal.WriteInt32(item, StableDiffusionNative.ImageLayout.CHANNELS, 3);
                    Marshal.WriteIntPtr(item, StableDiffusionNative.ImageLayout.DATA, pixels);
                }

                Marshal.WriteIntPtr(parameters, StableDiffusionNative.ImageOffsets.REF_IMAGES, references);
                Marshal.WriteInt32(parameters, StableDiffusionNative.ImageOffsets.REF_IMAGES_COUNT, photos.Count);
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

            foreach (IntPtr allocation in allocations)
            {
                Marshal.FreeHGlobal(allocation);
            }

            foreach (IntPtr text in strings)
            {
                Marshal.FreeCoTaskMem(text);
            }

            if (references != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(references);
            }

            if (loras != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(loras);
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
