using HomiliaMessenger.Business.Modules.Agents;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.ImageGeneration;

/// <summary>
/// A graphics card the image generation runtime can draw on.
/// </summary>
/// <param name="Name">The runtime's name for it, such as <c>CUDA0</c> or <c>Vulkan0</c>.</param>
/// <param name="Description">The card, such as "NVIDIA GeForce RTX 4070 Ti".</param>
/// <param name="TotalBytes">Its memory, or zero when the runtime cannot tell.</param>
/// <param name="IsIntegrated">Whether it shares the computer's memory, as a laptop's built-in graphics do.</param>
public sealed record ImageGpuDevice(string Name, string Description, long TotalBytes, bool IsIntegrated)
{
    /// <summary>
    /// Whether the runtime reaches it through CUDA, NVIDIA's own interface, rather than Vulkan.
    /// </summary>
    public bool IsCuda => Name.StartsWith("CUDA", StringComparison.OrdinalIgnoreCase);

    public bool IsNvidia => IsCuda || Description.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The graphics cards image models can run on, and the optional CUDA runtime for NVIDIA cards.
/// </summary>
/// <param name="Devices">The cards the runtime in use sees, the one graphics card models run on first.</param>
/// <param name="IsCudaOffered">Whether the CUDA runtime can be downloaded here: on 64-bit Windows with an NVIDIA card.</param>
/// <param name="IsCudaDownloaded">Whether the CUDA runtime is downloaded.</param>
/// <param name="IsCudaActive">Whether the runtime in use is the CUDA one; a download is used from the next start.</param>
/// <param name="CudaDownloadBytes">What downloading the CUDA runtime takes.</param>
/// <param name="CudaDiskBytes">What the CUDA runtime takes on disk, once unpacked.</param>
/// <param name="Problem">Why graphics cards cannot be used, when that is so.</param>
public sealed record ImageGpuStatus(
    IReadOnlyList<ImageGpuDevice> Devices,
    bool IsCudaOffered,
    bool IsCudaDownloaded,
    bool IsCudaActive,
    long CudaDownloadBytes,
    long CudaDiskBytes,
    string? Problem)
{
    public ImageGpuDevice? Preferred => Devices.Count == 0 ? null : Devices[0];

    public bool HasGpu => Devices.Count > 0;
}

/// <summary>
/// Capability to find the graphics cards image models can run on, and to download the CUDA runtime that makes NVIDIA cards
/// several times faster than the Vulkan backend included with the app.
/// </summary>
public interface IImageGpuRuntime
{
    /// <summary>
    /// Lists the graphics cards the runtime sees. Loads the runtime the first time, which takes a moment.
    /// </summary>
    ImageGpuStatus GetStatus();

    /// <summary>
    /// Downloads, verifies, and unpacks the CUDA runtime, resuming an interrupted download. It is used from the next start
    /// of the app, or at once when no image has been generated yet.
    /// </summary>
    Task<LocalModelDownloadResult> DownloadCudaAsync(IProgress<LocalModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the CUDA runtime, unless it is in use; returns whether it was removed.
    /// </summary>
    Task<bool> DeleteCudaAsync(CancellationToken cancellationToken = default);
}
