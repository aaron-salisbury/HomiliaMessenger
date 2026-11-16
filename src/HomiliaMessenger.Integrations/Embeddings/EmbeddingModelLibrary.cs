using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Memories;
using HomiliaMessenger.Integrations.LocalModels;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.Embeddings;

/// <summary>
/// Downloads embedding models into their own folder, apart from conversational models, and remembers which one memories
/// are searched with.
/// </summary>
/// <remarks>
/// Downloads work as they do for conversational models (see <see cref="LocalModelLibrary"/>). The chosen model's ID is
/// kept in <c>selected-model.txt</c> beside the model folders.
/// </remarks>
internal sealed class EmbeddingModelLibrary : IEmbeddingModelLibrary
{
    private const string SELECTED_FILE = "selected-model.txt";

    private readonly IReadOnlyList<EmbeddingModelDefinition> _catalog;
    private readonly string _directory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlamaEmbeddingHost? _host;
    private readonly ILogger<EmbeddingModelLibrary> _logger;
    private readonly ConcurrentDictionary<string, bool> _downloading = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _selectionLock = new();
    private string? _selectedId;

    public EmbeddingModelLibrary(string directory, IHttpClientFactory httpClientFactory, LlamaEmbeddingHost host, ILogger<EmbeddingModelLibrary> logger)
        : this(EmbeddingModelCatalog.Models, directory, httpClientFactory, logger, host)
    {
    }

    internal EmbeddingModelLibrary(IReadOnlyList<EmbeddingModelDefinition> catalog, string directory, IHttpClientFactory httpClientFactory, ILogger<EmbeddingModelLibrary> logger, LlamaEmbeddingHost? host = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _host = host;
        _selectedId = LoadSelection();
    }

    internal IReadOnlyList<EmbeddingModelDefinition> Catalog => _catalog;

    /// <summary>
    /// The chosen model, once it is downloaded; <see langword="null"/> until then.
    /// </summary>
    internal EmbeddingModelDefinition? Active
    {
        get
        {
            EmbeddingModelDefinition? selected = Find(_selectedId);

            return selected is not null && IsDownloaded(selected) ? selected : null;
        }
    }

    public IReadOnlyList<EmbeddingModel> List()
    {
        string? selected = _selectedId;

        return [.. _catalog.Select(model => new EmbeddingModel(
            model.Id, model.DisplayName, model.Description, model.License, model.File.Size, IsDownloaded(model),
            string.Equals(model.Id, selected, StringComparison.OrdinalIgnoreCase)))];
    }

    internal EmbeddingModelDefinition? Find(string? id)
    {
        return _catalog.FirstOrDefault(model => string.Equals(model.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    internal string ModelDirectory(EmbeddingModelDefinition model)
    {
        return Path.Combine(_directory, model.Id);
    }

    internal bool IsDownloaded(EmbeddingModelDefinition model)
    {
        FileInfo info = new(Path.Combine(ModelDirectory(model), model.File.LocalName));

        return info.Exists && info.Length == model.File.Size;
    }

    public bool Select(string modelId)
    {
        EmbeddingModelDefinition? model = Find(modelId);

        if (model is null)
        {
            return false;
        }

        lock (_selectionLock)
        {
            SaveSelection(model.Id);
            _selectedId = model.Id;
        }

        _logger.LogInformation("Memories are now searched with embedding model {ModelId}.", model.Id);

        return true;
    }

    public async Task<LocalModelDownloadResult> DownloadAsync(string modelId, IProgress<LocalModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        EmbeddingModelDefinition? model = Find(modelId);

        if (model is null)
        {
            return LocalModelDownloadResult.Failed($"There is no embedding model named \"{modelId}\".");
        }

        if (!IsDownloaded(model))
        {
            if (!_downloading.TryAdd(model.Id, true))
            {
                return LocalModelDownloadResult.Failed($"{model.DisplayName} is already downloading.");
            }

            try
            {
                LocalModelDownloadResult result = await LocalModelLibrary.DownloadFilesAsync(ModelDirectory(model), model.BaseUrl, model.Files, model.DisplayName, _httpClientFactory.CreateClient(), null, progress, cancellationToken);

                if (result.Status != LocalModelDownloadStatus.Success)
                {
                    return result;
                }

                _logger.LogInformation("Downloaded embedding model {ModelId}.", model.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return LocalModelDownloadResult.Canceled;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException)
            {
                _logger.LogWarning(error, "Downloading embedding model {ModelId} failed.", model.Id);
                return LocalModelDownloadResult.Failed($"Could not download {model.DisplayName}: {error.Message} Try again to resume.");
            }
            finally
            {
                _downloading.TryRemove(model.Id, out _);
            }
        }

        // A downloaded model is used when none is chosen, or the chosen one is not downloaded, until the user chooses.
        if (Active is null)
        {
            Select(model.Id);
        }

        return LocalModelDownloadResult.Success;
    }

    public async Task<bool> DeleteAsync(string modelId, CancellationToken cancellationToken = default)
    {
        EmbeddingModelDefinition? model = Find(modelId);

        if (model is null)
        {
            return false;
        }

        string directory = ModelDirectory(model);
        string partial = directory + LocalModelLibrary.PARTIAL_SUFFIX;
        bool existed = Directory.Exists(directory);

        void DeleteFiles()
        {
            if (existed)
            {
                Directory.Delete(directory, recursive: true);
            }

            if (Directory.Exists(partial) && !_downloading.ContainsKey(model.Id))
            {
                Directory.Delete(partial, recursive: true);
            }
        }

        // A loaded model's file is locked on Windows, so delete it while the host holds the model unloaded. Stored vectors
        // are kept: choosing the model again later makes them comparable again without embedding everything anew.
        await Task.Run(() => _host is null ? Task.Run(DeleteFiles) : _host.UnloadAsync(directory, DeleteFiles), cancellationToken);

        return existed;
    }

    private string? LoadSelection()
    {
        string path = Path.Combine(_directory, SELECTED_FILE);

        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(error, "Could not read the chosen embedding model from {Path}.", path);
            return null;
        }
    }

    private void SaveSelection(string modelId)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, SELECTED_FILE);
        string temporary = path + ".tmp";

        File.WriteAllText(temporary, modelId);
        File.Move(temporary, path, overwrite: true);
    }
}
