using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Integrations.Downloads;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException)
        {
            _logger.LogWarning(error, "Downloading local model {ModelId} failed.", model.Id);
            return LocalModelDownloadResult.Failed($"Could not download {model.DisplayName}: {error.Message} Try again to resume.");
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

        long downloaded = model.Files.Sum(file => Math.Min(FileDownload.ExistingLength(Path.Combine(partial, file.Name)), file.Size));

        if (FileDownload.MissingSpace(partial, model.DownloadBytes - downloaded, model.DisplayName) is { } problem)
        {
            return LocalModelDownloadResult.Failed(problem);
        }

        DownloadProgress reporter = new(progress is null ? null : (bytes, total) => progress.Report(new LocalModelDownloadProgress(bytes, total)), model.DownloadBytes, downloaded);
        HttpClient client = _httpClientFactory.CreateClient();

        foreach (LocalModelFile file in model.Files)
        {
            await FileDownload.DownloadAsync(client, new Uri(model.BaseUrl + file.Name), Path.Combine(partial, file.Name), file.Size, reporter, cancellationToken);
        }

        reporter.Report(force: true);

        foreach (LocalModelFile file in model.Files)
        {
            string path = Path.Combine(partial, file.Name);

            if (!string.Equals(await FileDownload.HashAsync(path, cancellationToken), file.Sha256, StringComparison.OrdinalIgnoreCase))
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
}
