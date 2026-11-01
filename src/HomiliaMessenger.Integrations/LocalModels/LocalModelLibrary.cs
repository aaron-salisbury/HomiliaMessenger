using HomiliaMessenger.Business.Modules.Agents;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.LocalModels;

/// <summary>
/// Downloads catalog models into the application data directory and tracks which are present.
/// </summary>
/// <remarks>
/// Files download into a <c>{id}.download</c> folder and are checked against their published SHA-256 hashes before the
/// folder is renamed to <c>{id}</c>, so a model folder only exists once it is complete. A partial download is kept so the
/// next attempt resumes it.
/// </remarks>
internal sealed class LocalModelLibrary : ILocalModelLibrary
{
    internal const string PROVIDER = "Local";
    private const string PARTIAL_SUFFIX = ".download";
    private const int BUFFER_SIZE = 1024 * 1024;

    // A download that receives nothing for this long is treated as failed (and can be resumed).
    private static readonly TimeSpan STALL_TIMEOUT = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PROGRESS_INTERVAL = TimeSpan.FromMilliseconds(200);

    private readonly IReadOnlyList<LocalModelDefinition> _catalog;
    private readonly string _modelsDirectory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OnnxModelHost _host;
    private readonly ILogger<LocalModelLibrary> _logger;
    private readonly ConcurrentDictionary<string, bool> _downloading = new(StringComparer.OrdinalIgnoreCase);

    public LocalModelLibrary(string modelsDirectory, IHttpClientFactory httpClientFactory, OnnxModelHost host, ILogger<LocalModelLibrary> logger)
        : this(LocalModelCatalog.Models, modelsDirectory, httpClientFactory, host, logger)
    {
    }

    internal LocalModelLibrary(IReadOnlyList<LocalModelDefinition> catalog, string modelsDirectory, IHttpClientFactory httpClientFactory, OnnxModelHost host, ILogger<LocalModelLibrary> logger)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        _modelsDirectory = modelsDirectory;
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Provider => PROVIDER;

    public IReadOnlyList<LocalModel> List()
    {
        return [.. _catalog.Select(model => new LocalModel(
            model.Id, model.DisplayName, model.Description, model.License, model.DownloadBytes, IsDownloaded(model)))];
    }

    internal IReadOnlyList<LocalModelDefinition> Catalog => _catalog;

    internal LocalModelDefinition? Find(string? id)
    {
        return _catalog.FirstOrDefault(model => string.Equals(model.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    internal string ModelDirectory(LocalModelDefinition model)
    {
        return Path.Combine(_modelsDirectory, model.Id);
    }

    /// <summary>
    /// Whether every file is present at its expected size. Hashes are checked once, when the download completes.
    /// </summary>
    internal bool IsDownloaded(LocalModelDefinition model)
    {
        string directory = ModelDirectory(model);

        return model.Files.All(file =>
        {
            FileInfo info = new(Path.Combine(directory, file.Name));
            return info.Exists && info.Length == file.Size;
        });
    }

    public async Task<LocalModelDownloadResult> DownloadAsync(string modelId, IProgress<LocalModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        LocalModelDefinition? model = Find(modelId);

        if (model is null)
        {
            return LocalModelDownloadResult.Failed($"There is no local model named \"{modelId}\".");
        }

        if (IsDownloaded(model))
        {
            return LocalModelDownloadResult.Success;
        }

        if (!_downloading.TryAdd(model.Id, true))
        {
            return LocalModelDownloadResult.Failed($"{model.DisplayName} is already downloading.");
        }

        try
        {
            return await DownloadCoreAsync(model, progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LocalModelDownloadResult.Canceled;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // A stall surfaces as a cancellation the caller did not request.
            _logger.LogWarning(error, "Downloading local model {ModelId} failed.", model.Id);
            string problem = error is OperationCanceledException ? "The download stopped receiving data." : error.Message;
            return LocalModelDownloadResult.Failed($"Could not download {model.DisplayName}: {problem} Try again to resume.");
        }
        finally
        {
            _downloading.TryRemove(model.Id, out _);
        }
    }

    public async Task<bool> DeleteAsync(string modelId, CancellationToken cancellationToken = default)
    {
        LocalModelDefinition? model = Find(modelId);

        if (model is null)
        {
            return false;
        }

        string directory = ModelDirectory(model);
        string partial = directory + PARTIAL_SUFFIX;
        bool existed = Directory.Exists(directory);

        // Loaded model files are locked on Windows, so delete them while the host holds the model unloaded; a reply
        // queued meanwhile then finds the files gone rather than reloading them.
        await Task.Run(() => _host.UnloadAsync(directory, () =>
        {
            if (existed)
            {
                Directory.Delete(directory, recursive: true);
            }

            if (Directory.Exists(partial) && !_downloading.ContainsKey(model.Id))
            {
                Directory.Delete(partial, recursive: true);
            }
        }), cancellationToken);

        return existed;
    }

    private async Task<LocalModelDownloadResult> DownloadCoreAsync(LocalModelDefinition model, IProgress<LocalModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        string directory = ModelDirectory(model);
        string partial = directory + PARTIAL_SUFFIX;
        Directory.CreateDirectory(partial);

        long downloaded = model.Files.Sum(file => Math.Min(ExistingLength(Path.Combine(partial, file.Name)), file.Size));
        long remaining = model.DownloadBytes - downloaded;
        long available = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(partial))!).AvailableFreeSpace;

        if (available < remaining)
        {
            return LocalModelDownloadResult.Failed($"{model.DisplayName} needs {FormatGigabytes(remaining)} more disk space; {FormatGigabytes(available)} is free.");
        }

        ProgressReporter reporter = new(progress, model.DownloadBytes, downloaded);
        HttpClient client = _httpClientFactory.CreateClient();

        foreach (LocalModelFile file in model.Files)
        {
            await DownloadFileAsync(client, new Uri(model.BaseUrl + file.Name), Path.Combine(partial, file.Name), file.Size, reporter, cancellationToken);
        }

        reporter.Report(force: true);

        foreach (LocalModelFile file in model.Files)
        {
            string path = Path.Combine(partial, file.Name);

            if (!string.Equals(await HashAsync(path, cancellationToken), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                return LocalModelDownloadResult.Failed($"{file.Name} did not match its published checksum and was discarded. Try again to download it.");
            }
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.Move(partial, directory);
        _logger.LogInformation("Downloaded local model {ModelId}.", model.Id);

        return LocalModelDownloadResult.Success;
    }

    private static async Task DownloadFileAsync(HttpClient client, Uri url, string path, long size, ProgressReporter reporter, CancellationToken cancellationToken)
    {
        long existing = ExistingLength(path);

        if (existing == size)
        {
            return;
        }

        if (existing > size)
        {
            File.Delete(path);
            // Counted toward progress at its expected size.
            reporter.Add(-size);
            existing = 0;
        }

        using HttpRequestMessage request = new(HttpMethod.Get, url);

        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(STALL_TIMEOUT);

        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        response.EnsureSuccessStatusCode();

        // A server that ignores the range sends the whole file, so start the file over.
        bool append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;

        if (existing > 0 && !append)
        {
            reporter.Add(-existing);
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(stall.Token);
        await using FileStream target = new(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BUFFER_SIZE, useAsync: true);
        byte[] buffer = new byte[BUFFER_SIZE];
        int read;

        while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
        {
            stall.CancelAfter(STALL_TIMEOUT);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            reporter.Add(read);
            reporter.Report();
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BUFFER_SIZE, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static long ExistingLength(string path)
    {
        FileInfo info = new(path);
        return info.Exists ? info.Length : 0;
    }

    private static string FormatGigabytes(long bytes)
    {
        return $"{bytes / 1_000_000_000d:0.0} GB";
    }

    /// <summary>
    /// Reports overall progress at most every <see cref="PROGRESS_INTERVAL"/>.
    /// </summary>
    private sealed class ProgressReporter
    {
        private readonly IProgress<LocalModelDownloadProgress>? _progress;
        private readonly long _total;
        private readonly Stopwatch _sinceReport = Stopwatch.StartNew();
        private long _downloaded;

        internal ProgressReporter(IProgress<LocalModelDownloadProgress>? progress, long total, long downloaded)
        {
            _progress = progress;
            _total = total;
            _downloaded = downloaded;
            Report(force: true);
        }

        internal void Add(long bytes)
        {
            _downloaded += bytes;
        }

        internal void Report(bool force = false)
        {
            if (_progress is not null && (force || _sinceReport.Elapsed >= PROGRESS_INTERVAL))
            {
                _progress.Report(new LocalModelDownloadProgress(_downloaded, _total));
                _sinceReport.Restart();
            }
        }
    }
}
