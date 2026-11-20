using HomiliaMessenger.Build;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Integrations.LocalModels;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.ImageGeneration;

/// <summary>
/// The CUDA build of the pinned stable-diffusion.cpp release, downloaded in Settings for NVIDIA graphics cards, and the
/// graphics cards the runtime sees.
/// </summary>
/// <remarks>
/// <para>
/// The Vulkan backend included with the app draws on any graphics card, but the CUDA build is several times faster on an
/// NVIDIA card: through this app on an RTX 4070 Ti, a FLUX.2 [klein] photo in about 4 seconds rather than 24 and a selfie
/// in 7 rather than 13. The CUDA backend cannot be added to the included runtime: the runtime's own library is built
/// differently for CUDA (in the experiment, the included library with the CUDA backend sampled four times slower), so the
/// CUDA build is used whole, in place of the included one, and only one can be loaded in a process.
/// </para>
/// <para>
/// It is too large to include with the app (about 1 GB to download, 1.3 GB unpacked, most of it NVIDIA's CUDA libraries,
/// redistributed under NVIDIA's CUDA license), so it downloads like a model: both release archives are checked against
/// their published SHA-256 hashes, the libraries are unpacked, and their private ggml is renamed (<c>sdml</c>) exactly as
/// the build does for the included runtime, so it never collides with the ggml conversation models load.
/// </para>
/// </remarks>
internal sealed class CudaRuntime : IImageGpuRuntime
{
    internal const string DIRECTORY_NAME = "cuda12-win-x64-" + StableDiffusionNative.PINNED_COMMIT;
    internal const string STAMP = "runtime.stamp";
    internal const string LICENSE_URL = "https://docs.nvidia.com/cuda/eula/index.html";

    internal const string RELEASE_URL = "https://github.com/leejet/stable-diffusion.cpp/releases/download/master-1104-5dcfa79/";
    internal static readonly LocalModelFile RUNTIME_ARCHIVE = new("sd-master-5dcfa79-bin-win-cuda12-x64.zip", 412309812, "c7132d6f7e36dee6c39d3d51e8970470c615e5c9c8db498d6f73d5c14b4b1f5d");
    internal static readonly LocalModelFile CUDA_ARCHIVE = new("cudart-sd-bin-win-cu12-x64.zip", 563452046, "fe20366827d357c00797eebb58244dddab7fd9a348d70090c3871004c320f38d");

    /// <summary>
    /// What the unpacked runtime takes on disk, as measured.
    /// </summary>
    internal const long DISK_BYTES = 1_295_000_000;

    private static readonly Dictionary<string, string> RENAMES = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ggml.dll"] = "sdml.dll",
        ["ggml-base.dll"] = "sdml-base.dll"
    };

    private readonly string _directory;
    private readonly IStableDiffusionBackend _backend;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CudaRuntime> _logger;
    private readonly SemaphoreSlim _busy = new(1, 1);

    /// <param name="directory">The folder runtimes are kept in, beside image models.</param>
    public CudaRuntime(string directory, IStableDiffusionBackend backend, IHttpClientFactory httpClientFactory, ILogger<CudaRuntime> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Where the runtime is unpacked.
    /// </summary>
    internal string RuntimeDirectory => Path.Combine(_directory, DIRECTORY_NAME);

    private string ArchiveDirectory => RuntimeDirectory + "-archives";

    internal static long DownloadBytes => RUNTIME_ARCHIVE.Size + CUDA_ARCHIVE.Size;

    /// <summary>
    /// Whether the CUDA runtime can run here at all: 64-bit Windows, for which the release has a CUDA build.
    /// </summary>
    internal static bool IsSupportedSystem => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>
    /// Whether <paramref name="directory"/> holds a completely unpacked CUDA runtime, which is stamped last.
    /// </summary>
    internal static bool IsComplete(string directory)
    {
        return File.Exists(Path.Combine(directory, STAMP)) && IsCompleteExceptStamp(directory);
    }

    public ImageGpuStatus GetStatus()
    {
        if (_backend.RuntimeProblem is { } problem)
        {
            return new ImageGpuStatus([], false, false, false, DownloadBytes, DISK_BYTES, problem);
        }

        IReadOnlyList<ImageGpuDevice> devices = _backend.GpuDevices();
        RemoveUnstamped();
        bool downloaded = IsComplete(RuntimeDirectory);
        bool offered = IsSupportedSystem && (downloaded || devices.Any(device => device.IsNvidia));

        return new ImageGpuStatus(devices, offered, downloaded, _backend.IsCudaActive, DownloadBytes, DISK_BYTES,
            devices.Count == 0 ? "No graphics card the image generation runtime can use was found." : null);
    }

    public async Task<LocalModelDownloadResult> DownloadCudaAsync(IProgress<LocalModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsSupportedSystem)
        {
            return LocalModelDownloadResult.Failed("The CUDA runtime is available for 64-bit Windows only.");
        }

        if (IsComplete(RuntimeDirectory))
        {
            return LocalModelDownloadResult.Success;
        }

        if (!await _busy.WaitAsync(0, cancellationToken))
        {
            return LocalModelDownloadResult.Failed("The CUDA runtime is already downloading.");
        }

        try
        {
            // Archives already downloaded and verified (an unpack failed after) are unpacked again without downloading.
            bool verified = await Task.Run(() => new[] { RUNTIME_ARCHIVE, CUDA_ARCHIVE }.All(IsVerifiedArchive), cancellationToken);

            if (!verified)
            {
                LocalModelDownloadResult downloaded = await LocalModelLibrary.DownloadFilesAsync(
                    ArchiveDirectory, RELEASE_URL, [RUNTIME_ARCHIVE, CUDA_ARCHIVE], "The CUDA runtime", _httpClientFactory.CreateClient(), null, progress, cancellationToken);

                if (downloaded.Status != LocalModelDownloadStatus.Success)
                {
                    return downloaded;
                }
            }

            await Task.Run(Unpack, cancellationToken);
            _logger.LogInformation("Downloaded and unpacked the CUDA image generation runtime into {Directory}.", RuntimeDirectory);

            return LocalModelDownloadResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LocalModelDownloadResult.Canceled;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException)
        {
            _logger.LogWarning(error, "Downloading the CUDA image generation runtime failed.");
            return LocalModelDownloadResult.Failed($"Could not download the CUDA runtime: {error.Message} Try again to resume.");
        }
        finally
        {
            _busy.Release();
        }
    }

    public async Task<bool> DeleteCudaAsync(CancellationToken cancellationToken = default)
    {
        // Loaded libraries cannot be removed. Unstamped, the runtime is no longer loaded at the next start, which uses the
        // included runtime and removes what is left (see GetStatus).
        if (_backend.IsCudaActive)
        {
            File.Delete(Path.Combine(RuntimeDirectory, STAMP));
            _logger.LogInformation("The CUDA image generation runtime is in use; it is removed at the next start.");
            return false;
        }

        await _busy.WaitAsync(cancellationToken);

        try
        {
            bool existed = false;

            foreach (string directory in new[] { RuntimeDirectory, ArchiveDirectory, ArchiveDirectory + LocalModelLibrary.PARTIAL_SUFFIX, RuntimeDirectory + ".tmp" })
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                    existed = true;
                }
            }

            _logger.LogInformation("Removed the CUDA image generation runtime.");

            return existed;
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>
    /// Unpacks the libraries from the verified archives into a temporary folder, renames their private ggml, stamps it, and
    /// moves it into place; then removes the archives.
    /// </summary>
    private void Unpack()
    {
        string temporary = RuntimeDirectory + ".tmp";

        if (Directory.Exists(temporary))
        {
            Directory.Delete(temporary, recursive: true);
        }

        Directory.CreateDirectory(temporary);

        using (ZipArchive runtime = ZipFile.OpenRead(Path.Combine(ArchiveDirectory, RUNTIME_ARCHIVE.LocalName)))
        {
            foreach (ZipArchiveEntry entry in runtime.Entries)
            {
                string name = entry.Name;

                if (name.Equals("stable-diffusion.dll", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ggml", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] image = Read(entry);
                    NativeLibraryRenamer.Rename(image, RENAMES);
                    string target = RENAMES.TryGetValue(name, out string? renamed) ? renamed : name;
                    File.WriteAllBytes(Path.Combine(temporary, target), image);
                }
                else if (name is "stable-diffusion.cpp.txt" or "ggml.txt" or "cutlass.txt")
                {
                    entry.ExtractToFile(Path.Combine(temporary, "LICENSE." + Path.GetFileNameWithoutExtension(name) + ".txt"));
                }
            }
        }

        using (ZipArchive cuda = ZipFile.OpenRead(Path.Combine(ArchiveDirectory, CUDA_ARCHIVE.LocalName)))
        {
            foreach (ZipArchiveEntry entry in cuda.Entries.Where(entry => StableDiffusionNative.CUDA_DEPENDENCY_FILES.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)))
            {
                entry.ExtractToFile(Path.Combine(temporary, entry.Name));
            }
        }

        // The runtime's own library must now name the renamed ggml, as the build checks for the included runtime.
        IReadOnlyList<string> names = NativeLibraryRenamer.Names(File.ReadAllBytes(Path.Combine(temporary, "stable-diffusion.dll")));

        if (!names.Contains("sdml.dll", StringComparer.OrdinalIgnoreCase) || !names.Contains("sdml-base.dll", StringComparer.OrdinalIgnoreCase)
            || !IsCompleteExceptStamp(temporary))
        {
            Directory.Delete(temporary, recursive: true);
            throw new InvalidDataException("The CUDA runtime's archives did not hold the expected libraries.");
        }

        File.WriteAllText(Path.Combine(temporary, STAMP), $"stable-diffusion.cpp {StableDiffusionNative.PINNED_COMMIT} {RUNTIME_ARCHIVE.Name} {RUNTIME_ARCHIVE.Sha256} {CUDA_ARCHIVE.Name} {CUDA_ARCHIVE.Sha256}");

        if (Directory.Exists(RuntimeDirectory))
        {
            Directory.Delete(RuntimeDirectory, recursive: true);
        }

        Directory.Move(temporary, RuntimeDirectory);
        Directory.Delete(ArchiveDirectory, recursive: true);
    }

    /// <summary>
    /// Removes an unpacked runtime left without its stamp (removed while in use, or found unable to use a card) once it is
    /// not loaded. An unpack in progress works in its own folder and is moved here only once stamped.
    /// </summary>
    private void RemoveUnstamped()
    {
        if (_backend.IsCudaActive || !Directory.Exists(RuntimeDirectory) || File.Exists(Path.Combine(RuntimeDirectory, STAMP)) || !_busy.Wait(0))
        {
            return;
        }

        try
        {
            Directory.Delete(RuntimeDirectory, recursive: true);
            _logger.LogInformation("Removed the CUDA image generation runtime.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(error, "Could not remove the CUDA image generation runtime from {Directory}.", RuntimeDirectory);
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>
    /// Whether an archive kept from an earlier download still matches its published hash; one that does not is removed,
    /// to be downloaded again.
    /// </summary>
    private bool IsVerifiedArchive(LocalModelFile file)
    {
        string path = Path.Combine(ArchiveDirectory, file.LocalName);

        if (new FileInfo(path) is not { Exists: true } archive || archive.Length != file.Size)
        {
            return false;
        }

        using (FileStream stream = File.OpenRead(path))
        {
            if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)) == file.Sha256.ToLowerInvariant())
            {
                return true;
            }
        }

        File.Delete(path);
        return false;
    }

    private static bool IsCompleteExceptStamp(string directory)
    {
        return StableDiffusionNative.LibraryFiles().Concat(StableDiffusionNative.CUDA_DEPENDENCY_FILES).Append("ggml-cuda.dll")
            .All(file => File.Exists(Path.Combine(directory, file)));
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using MemoryStream copy = new((int)entry.Length);
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
