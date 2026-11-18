using System;
using System.IO;
using System.Runtime.InteropServices;

namespace HomiliaMessenger.Integrations.ImageGeneration;

/// <summary>
/// The C API of stable-diffusion.cpp, pinned to commit <see cref="PINNED_COMMIT"/>, loaded from the private copy built
/// with the app (see <c>build/native/StableDiffusionRuntime.targets</c>).
/// </summary>
/// <remarks>
/// <para>
/// Struct layouts follow <c>include/stable-diffusion.h</c> at that commit, as the local portrait experiment's in-process
/// probe did. Parameter structs are allocated larger than the native ones and filled in by the library's own
/// <c>*_init</c> functions, so only the leading fields this app writes are declared here. <see cref="Load"/> refuses a
/// library built from another commit, and checks that the declared fields hold the library's documented defaults before
/// anything is written to them.
/// </para>
/// <para>
/// Upgrading the runtime means: reading the new header, updating the prefix structs and default checks, updating the
/// pinned commit and release hashes in the build, and rerunning the <c>ImageModel</c> tests against the real model.
/// </para>
/// </remarks>
internal sealed class StableDiffusionNative
{
    internal const string PINNED_COMMIT = "5dcfa79";

    /// <summary>
    /// The folder, under the app's own, holding the runtime for each system (<c>win-x64</c>, <c>linux-x64</c>).
    /// </summary>
    internal const string DIRECTORY_NAME = "StableDiffusion";

    /// <summary>
    /// Larger than <c>sd_ctx_params_t</c> and <c>sd_img_gen_params_t</c> at the pinned commit (both under 1 KiB).
    /// </summary>
    internal const int PARAMS_BUFFER_BYTES = 4096;

    internal const int SD_TYPE_COUNT = 45;
    internal const int CUDA_RNG = 1;
    internal const int SAMPLE_METHOD_COUNT = 21;
    internal const int SCHEDULER_COUNT = 17;
    internal const int SD_CANCEL_ALL = 0;

    /// <summary>
    /// <c>SD_TYPE_Q8_0</c>, the weight format face matching's image encoder is converted to.
    /// </summary>
    internal const int SD_TYPE_Q8_0 = 8;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void LogCallback(int level, IntPtr text, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void GenerationProgressCallback(IntPtr progress, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LoadBackendsFunction(IntPtr directory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr StringFunction();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SetLogCallbackFunction(LogCallback? callback, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SetGenerationProgressCallbackFunction(IntPtr context, GenerationProgressCallback? callback, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void InitParamsFunction(IntPtr parameters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr NewContextFunction(IntPtr parameters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FreeFunction(IntPtr pointer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int CoresFunction();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int NameToEnumFunction(IntPtr name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr EnumToNameFunction(int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int DefaultSchedulerFunction(IntPtr context, int sampleMethod);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal delegate bool ContextQueryFunction(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal delegate bool GenerateImageFunction(IntPtr context, IntPtr parameters, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void CancelFunction(IntPtr context, int mode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal delegate bool ConvertFunction(IntPtr modelPath, IntPtr clipLPath, IntPtr clipGPath, IntPtr t5xxlPath, IntPtr diffusionModelPath, IntPtr vaePath, IntPtr outputPath, int outputType, IntPtr tensorTypeRules, [MarshalAs(UnmanagedType.I1)] bool convertName, int threads, IntPtr loras, int loraCount);

    private StableDiffusionNative(IntPtr library, IntPtr ggml)
    {
        T Export<T>(IntPtr handle, string name) where T : Delegate
        {
            return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));
        }

        Commit = Export<StringFunction>(library, "sd_commit");
        SetLogCallback = Export<SetLogCallbackFunction>(library, "sd_set_log_callback");
        SetGenerationProgressCallback = Export<SetGenerationProgressCallbackFunction>(library, "sd_set_generation_progress_callback");
        ContextParamsInit = Export<InitParamsFunction>(library, "sd_ctx_params_init");
        NewContext = Export<NewContextFunction>(library, "new_sd_ctx");
        FreeContext = Export<FreeFunction>(library, "free_sd_ctx");
        PhysicalCores = Export<CoresFunction>(library, "sd_get_num_physical_cores");
        SupportsImageGeneration = Export<ContextQueryFunction>(library, "sd_ctx_supports_image_generation");
        ImageParamsInit = Export<InitParamsFunction>(library, "sd_img_gen_params_init");
        SampleMethodFromName = Export<NameToEnumFunction>(library, "str_to_sample_method");
        SchedulerFromName = Export<NameToEnumFunction>(library, "str_to_scheduler");
        SchedulerName = Export<EnumToNameFunction>(library, "sd_scheduler_name");
        DefaultScheduler = Export<DefaultSchedulerFunction>(library, "sd_get_default_scheduler");
        GenerateImage = Export<GenerateImageFunction>(library, "generate_image");
        FreeImageResult = Export<FreeFunction>(library, "free_sd_image_generation_result");
        Cancel = Export<CancelFunction>(library, "sd_cancel_generation");
        Convert = Export<ConvertFunction>(library, "convert_with_components");
        _loadBackends = Export<LoadBackendsFunction>(ggml, "ggml_backend_load_all_from_path");
    }

    private readonly LoadBackendsFunction _loadBackends;

    internal StringFunction Commit { get; }
    internal SetLogCallbackFunction SetLogCallback { get; }
    internal SetGenerationProgressCallbackFunction SetGenerationProgressCallback { get; }
    internal InitParamsFunction ContextParamsInit { get; }
    internal NewContextFunction NewContext { get; }
    internal FreeFunction FreeContext { get; }
    internal CoresFunction PhysicalCores { get; }
    internal ContextQueryFunction SupportsImageGeneration { get; }
    internal InitParamsFunction ImageParamsInit { get; }
    internal NameToEnumFunction SampleMethodFromName { get; }
    internal NameToEnumFunction SchedulerFromName { get; }
    internal EnumToNameFunction SchedulerName { get; }
    internal DefaultSchedulerFunction DefaultScheduler { get; }
    internal GenerateImageFunction GenerateImage { get; }
    internal FreeFunction FreeImageResult { get; }
    internal CancelFunction Cancel { get; }
    internal ConvertFunction Convert { get; }

    /// <summary>
    /// The folder holding the runtime for this system, under the app's folder, whether or not it is there.
    /// </summary>
    internal static string? RuntimeDirectory(string baseDirectory)
    {
        string? system = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : null;
        string? architecture = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x64" : null;

        return system is null || architecture is null ? null : Path.Combine(baseDirectory, DIRECTORY_NAME, $"{system}-{architecture}");
    }

    /// <summary>
    /// The runtime's libraries, in the order they must be loaded: each one's dependencies first. The private copies of
    /// ggml are renamed (<c>sdml</c>) so they never collide with the ggml LLamaSharp loads for conversation models.
    /// </summary>
    internal static string[] LibraryFiles()
    {
        return OperatingSystem.IsWindows()
            ? ["sdml-base.dll", "sdml.dll", "stable-diffusion.dll"]
            : ["libsdml-base.so.1", "libsdml.so.1", "libstable-diffusion.so"];
    }

    /// <summary>
    /// Loads the runtime from <paramref name="directory"/>, registers its CPU backends, and checks that it is the pinned
    /// build. The libraries stay loaded for the rest of the process; only models and contexts are released.
    /// </summary>
    /// <exception cref="DllNotFoundException">A library is missing or cannot be loaded on this system.</exception>
    /// <exception cref="InvalidOperationException">The runtime is not the pinned build, or its structs are not as declared.</exception>
    internal static StableDiffusionNative Load(string directory)
    {
        string[] files = LibraryFiles();
        IntPtr ggml = IntPtr.Zero;
        IntPtr library = IntPtr.Zero;

        foreach (string file in files)
        {
            IntPtr handle = NativeLibrary.Load(Path.Combine(directory, file));
            ggml = file.Contains("sdml.", StringComparison.Ordinal) ? handle : ggml;
            library = handle;
        }

        StableDiffusionNative native = new(library, ggml);
        string? commit = Marshal.PtrToStringUTF8(native.Commit());

        if (!string.Equals(commit, PINNED_COMMIT, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The image generation runtime is build {commit ?? "unknown"}, but this app was written for {PINNED_COMMIT}.");
        }

        native.CheckDefaults();

        // The CPU backend comes in variants for different instruction sets; ggml picks the best one this CPU supports.
        IntPtr path = Marshal.StringToCoTaskMemUTF8(directory);

        try
        {
            native._loadBackends(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }

        return native;
    }

    /// <summary>
    /// Checks that every field this app writes sits where the library's own initializers put their documented defaults,
    /// which would not hold if the struct layout differed from the pinned header.
    /// </summary>
    private void CheckDefaults()
    {
        IntPtr context = Marshal.AllocHGlobal(PARAMS_BUFFER_BYTES);
        IntPtr image = Marshal.AllocHGlobal(PARAMS_BUFFER_BYTES);

        try
        {
            Clear(context);
            Clear(image);
            ContextParamsInit(context);
            ImageParamsInit(image);

            bool contextMatches = Marshal.ReadIntPtr(context, ContextOffsets.MODEL_PATH) == IntPtr.Zero
                && Marshal.ReadIntPtr(context, ContextOffsets.CLIP_VISION) == IntPtr.Zero
                && Marshal.ReadIntPtr(context, ContextOffsets.IP_ADAPTER) == IntPtr.Zero
                && Marshal.ReadIntPtr(context, ContextOffsets.BACKEND) == IntPtr.Zero
                && Marshal.ReadInt32(context, ContextOffsets.THREADS) == PhysicalCores()
                && Marshal.ReadInt32(context, ContextOffsets.WEIGHT_TYPE) == SD_TYPE_COUNT
                && Marshal.ReadInt32(context, ContextOffsets.RNG_TYPE) == CUDA_RNG;
            bool imageMatches = Marshal.ReadIntPtr(image, ImageOffsets.PROMPT) == IntPtr.Zero
                && Marshal.ReadIntPtr(image, ImageOffsets.NEGATIVE_PROMPT) == IntPtr.Zero
                && Marshal.ReadInt32(image, ImageOffsets.CLIP_SKIP) == -1
                && Marshal.ReadInt32(image, ImageOffsets.WIDTH) == 512
                && Marshal.ReadInt32(image, ImageOffsets.HEIGHT) == 512
                && ReadSingle(image, ImageOffsets.TEXT_CFG) == 7f
                && Marshal.ReadInt32(image, ImageOffsets.SCHEDULER) == SCHEDULER_COUNT
                && Marshal.ReadInt32(image, ImageOffsets.SAMPLE_METHOD) == SAMPLE_METHOD_COUNT
                && Marshal.ReadInt32(image, ImageOffsets.SAMPLE_STEPS) == 20
                && ReadSingle(image, ImageOffsets.STRENGTH) == 0.75f
                && Marshal.ReadInt64(image, ImageOffsets.SEED) == -1
                && Marshal.ReadInt32(image, ImageOffsets.BATCH_COUNT) == 1
                && ReadSingle(image, ImageOffsets.CONTROL_STRENGTH) == 0.9f
                && Marshal.ReadIntPtr(image, ImageOffsets.IP_ADAPTER_IMAGE_DATA) == IntPtr.Zero
                && ReadSingle(image, ImageOffsets.IP_ADAPTER_STRENGTH) == 1f;

            if (!contextMatches || !imageMatches)
            {
                throw new InvalidOperationException("The image generation runtime's parameters are not laid out as this app expects.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(image);
            Marshal.FreeHGlobal(context);
        }
    }

    internal static void Clear(IntPtr buffer)
    {
        for (int offset = 0; offset < PARAMS_BUFFER_BYTES; offset += 8)
        {
            Marshal.WriteInt64(buffer, offset, 0);
        }
    }

    internal static float ReadSingle(IntPtr pointer, int offset)
    {
        return BitConverter.Int32BitsToSingle(Marshal.ReadInt32(pointer, offset));
    }

    internal static void WriteSingle(IntPtr pointer, int offset, float value)
    {
        Marshal.WriteInt32(pointer, offset, BitConverter.SingleToInt32Bits(value));
    }

    /// <summary>
    /// The leading fields of <c>sd_ctx_params_t</c>, through <c>backend</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ContextParamsPrefix
    {
        public IntPtr ModelPath, ClipL, ClipG, ClipVision, T5xxl, Llm, LlmVision, Diffusion, HighNoise, Uncond, EmbeddingsConnectors, Vae, AudioVae,
            AudioEncoder, Taesd, ControlNet, IpAdapter, MotionModule, Embeddings;
        public uint EmbeddingCount;
        public IntPtr PhotoMaker, Pulid, TensorTypeRules;
        public int Threads, WeightType, RngType, SamplerRngType, Prediction, LoraApplyMode;
        public byte Mmap, FlashAttn, DiffusionFlashAttn, TaePreviewOnly, DiffusionConvDirect, VaeConvDirect, ForceSdxlVaeConvScale;
        public int VaeFormat;
        public IntPtr MaxVram;
        public byte DisablePrefetch, EagerLoad;
        public IntPtr Backend;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Image
    {
        public uint Width, Height, Channel;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SlgParams
    {
        public IntPtr Layers;
        public nuint LayerCount;
        public float LayerStart, LayerEnd, Scale;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuidanceParams
    {
        public float TxtCfg, ImgCfg, DistilledGuidance;
        public SlgParams Slg;
    }

    /// <summary>
    /// The leading fields of <c>sd_img_gen_params_t</c>, through <c>ip_adapter_strength</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ImageParamsPrefix
    {
        public IntPtr Loras;
        public uint LoraCount;
        public IntPtr Prompt, NegativePrompt;
        public int ClipSkip;
        public Image InitImage;
        public IntPtr RefImages;
        public int RefImageCount;
        public IntPtr RefImageArgs;
        public Image MaskImage;
        public int Width, Height;
        public SampleParams SampleParams;
        public float Strength;
        public long Seed;
        public int BatchCount;
        public Image ControlImage;
        public float ControlStrength;
        public Image IpAdapterImage;
        public float IpAdapterStrength;
    }

    /// <summary>
    /// The whole of <c>sd_sample_params_t</c>, whose size places the fields after it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SampleParams
    {
        public GuidanceParams Guidance;
        public int Scheduler, SampleMethod, SampleSteps;
        public float Eta;
        public int ShiftedTimestep;
        public IntPtr CustomSigmas;
        public int CustomSigmasCount;
        public float FlowShift;
        public IntPtr ExtraSampleArgs;
    }

    /// <summary>
    /// <c>sd_generation_progress_t</c>, passed to the generation progress callback.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GenerationProgress
    {
        public IntPtr Stage, Detail, Unit;
        public int SamplePass;
        public long Completed, Total;
    }

    /// <summary>
    /// <c>sd_image_t</c> within <c>sd_image_result_t</c>, the first item of <c>sd_image_generation_result_t</c>.
    /// </summary>
    internal static class ResultOffsets
    {
        internal static readonly int ITEMS = 0;
        internal static readonly int COUNT = IntPtr.Size;
        internal static readonly int WIDTH = (int)Marshal.OffsetOf<Image>(nameof(Image.Width));
        internal static readonly int HEIGHT = (int)Marshal.OffsetOf<Image>(nameof(Image.Height));
        internal static readonly int CHANNELS = (int)Marshal.OffsetOf<Image>(nameof(Image.Channel));
        internal static readonly int DATA = (int)Marshal.OffsetOf<Image>(nameof(Image.Data));
    }

    internal static class ContextOffsets
    {
        internal static readonly int MODEL_PATH = (int)Marshal.OffsetOf<ContextParamsPrefix>(nameof(ContextParamsPrefix.ModelPath));
        internal static readonly int CLIP_VISION = (int)Marshal.OffsetOf<ContextParamsPrefix>(nameof(ContextParamsPrefix.ClipVision));
        internal static readonly int IP_ADAPTER = (int)Marshal.OffsetOf<ContextParamsPrefix>(nameof(ContextParamsPrefix.IpAdapter));
        internal static readonly int THREADS = (int)Marshal.OffsetOf<ContextParamsPrefix>(nameof(ContextParamsPrefix.Threads));
        internal static readonly int WEIGHT_TYPE = (int)Marshal.OffsetOf<ContextParamsPrefix>(nameof(ContextParamsPrefix.WeightType));
        internal static readonly int RNG_TYPE = (int)Marshal.OffsetOf<ContextParamsPrefix>(nameof(ContextParamsPrefix.RngType));
        internal static readonly int BACKEND = (int)Marshal.OffsetOf<ContextParamsPrefix>(nameof(ContextParamsPrefix.Backend));
    }

    internal static class ImageOffsets
    {
        private static readonly int SAMPLE = (int)Marshal.OffsetOf<ImageParamsPrefix>(nameof(ImageParamsPrefix.SampleParams));

        internal static readonly int PROMPT = (int)Marshal.OffsetOf<ImageParamsPrefix>(nameof(ImageParamsPrefix.Prompt));
        internal static readonly int NEGATIVE_PROMPT = (int)Marshal.OffsetOf<ImageParamsPrefix>(nameof(ImageParamsPrefix.NegativePrompt));
        internal static readonly int CLIP_SKIP = (int)Marshal.OffsetOf<ImageParamsPrefix>(nameof(ImageParamsPrefix.ClipSkip));
        internal static readonly int WIDTH = (int)Marshal.OffsetOf<ImageParamsPrefix>(nameof(ImageParamsPrefix.Width));
        internal static readonly int HEIGHT = (int)Marshal.OffsetOf<ImageParamsPrefix>(nameof(ImageParamsPrefix.Height));
        internal static readonly int TEXT_CFG = SAMPLE + (int)Marshal.OffsetOf<SampleParams>(nameof(SampleParams.Guidance))
            + (int)Marshal.OffsetOf<GuidanceParams>(nameof(GuidanceParams.TxtCfg));
        internal static readonly int SCHEDULER = SAMPLE + (int)Marshal.OffsetOf<SampleParams>(nameof(SampleParams.Scheduler));
        internal static readonly int SAMPLE_METHOD = SAMPLE + (int)Marshal.OffsetOf<SampleParams>(nameof(SampleParams.SampleMethod));
        internal static readonly int SAMPLE_STEPS = SAMPLE + (int)Marshal.OffsetOf<SampleParams>(nameof(SampleParams.SampleSteps));
        internal static readonly int STRENGTH = (int)Marshal.OffsetOf<ImageParamsPrefix>(nameof(ImageParamsPrefix.Strength));
        internal static readonly int SEED = (int)Marshal.OffsetOf<ImageParamsPrefix>(nameof(ImageParamsPrefix.Seed));
        internal static readonly int BATCH_COUNT = (int)Marshal.OffsetOf<ImageParamsPrefix>(nameof(ImageParamsPrefix.BatchCount));
        internal static readonly int CONTROL_STRENGTH = (int)Marshal.OffsetOf<ImageParamsPrefix>(nameof(ImageParamsPrefix.ControlStrength));
        internal static readonly int IP_ADAPTER_IMAGE = (int)Marshal.OffsetOf<ImageParamsPrefix>(nameof(ImageParamsPrefix.IpAdapterImage));
        internal static readonly int IP_ADAPTER_IMAGE_WIDTH = IP_ADAPTER_IMAGE + (int)Marshal.OffsetOf<Image>(nameof(Image.Width));
        internal static readonly int IP_ADAPTER_IMAGE_HEIGHT = IP_ADAPTER_IMAGE + (int)Marshal.OffsetOf<Image>(nameof(Image.Height));
        internal static readonly int IP_ADAPTER_IMAGE_CHANNELS = IP_ADAPTER_IMAGE + (int)Marshal.OffsetOf<Image>(nameof(Image.Channel));
        internal static readonly int IP_ADAPTER_IMAGE_DATA = IP_ADAPTER_IMAGE + (int)Marshal.OffsetOf<Image>(nameof(Image.Data));
        internal static readonly int IP_ADAPTER_STRENGTH = (int)Marshal.OffsetOf<ImageParamsPrefix>(nameof(ImageParamsPrefix.IpAdapterStrength));
    }
}
