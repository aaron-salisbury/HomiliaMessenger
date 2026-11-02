using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Credentials;
using HomiliaMessenger.Integrations.Downloads;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.LocalModels;

/// <summary>
/// Downloads models into the application data directory and tracks which are present: the built-in catalog, and GGUF
/// models the user adds from Hugging Face.
/// </summary>
/// <remarks>
/// Files download into a <c>{id}.download</c> folder and are checked against their published SHA-256 hashes before the
/// folder is renamed to <c>{id}</c>, so a model folder only exists once it is complete. A partial download is kept so the
/// next attempt resumes it. Added models are recorded in <c>user-models.json</c> beside their folders, pinned to the
/// repository revision they were added at.
/// </remarks>
internal sealed partial class LocalModelLibrary : ILocalModelLibrary
{
    internal const string PROVIDER = "Local";
    private const string PARTIAL_SUFFIX = ".download";
    private const string USER_MODELS_FILE = "user-models.json";

    private static readonly JsonSerializerOptions JSON = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly IReadOnlyList<LocalModelDefinition> _catalog;
    private readonly List<LocalModelDefinition> _userModels;
    private readonly Lock _userModelsLock = new();
    private readonly string _modelsDirectory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OnnxModelHost _host;
    private readonly LlamaModelHost? _llamaHost;
    private readonly HuggingFaceClient _huggingFace;
    private readonly ILogger<LocalModelLibrary> _logger;
    private readonly ConcurrentDictionary<string, bool> _downloading = new(StringComparer.OrdinalIgnoreCase);

    public LocalModelLibrary(string modelsDirectory, IHttpClientFactory httpClientFactory, OnnxModelHost host, LlamaModelHost llamaHost, ICredentialStore credentials, ILogger<LocalModelLibrary> logger)
        : this(LocalModelCatalog.Models, modelsDirectory, httpClientFactory, host, logger, llamaHost, credentials)
    {
    }

    internal LocalModelLibrary(IReadOnlyList<LocalModelDefinition> catalog, string modelsDirectory, IHttpClientFactory httpClientFactory, OnnxModelHost host, ILogger<LocalModelLibrary> logger, LlamaModelHost? llamaHost = null, ICredentialStore? credentials = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        _modelsDirectory = modelsDirectory;
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _llamaHost = llamaHost;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _huggingFace = new HuggingFaceClient(httpClientFactory, credentials);
        _userModels = LoadUserModels();
    }

    public string Provider => PROVIDER;

    public IReadOnlyList<LocalModel> List()
    {
        return [.. Catalog.Select(model => new LocalModel(
            model.Id, model.DisplayName, model.Description, model.License, model.DownloadBytes, IsDownloaded(model), model.IsUserAdded))];
    }

    /// <summary>
    /// Every model offered: the built-in ones, then those the user added.
    /// </summary>
    internal IReadOnlyList<LocalModelDefinition> Catalog
    {
        get
        {
            lock (_userModelsLock)
            {
                return [.. _catalog, .. _userModels];
            }
        }
    }

    internal LocalModelDefinition? Find(string? id)
    {
        return Catalog.FirstOrDefault(model => string.Equals(model.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public Task<HuggingFaceLookup> LookUpAsync(string repository, CancellationToken cancellationToken = default)
    {
        return _huggingFace.LookUpAsync(repository, cancellationToken);
    }

    public Task<bool> HasHuggingFaceTokenAsync(CancellationToken cancellationToken = default)
    {
        return _huggingFace.HasTokenAsync(cancellationToken);
    }

    public Task SetHuggingFaceTokenAsync(string? token, CancellationToken cancellationToken = default)
    {
        return _huggingFace.SetTokenAsync(token, cancellationToken);
    }

    public Task<LocalModelAddResult> AddAsync(HuggingFaceLookup lookup, GgufVariant variant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(variant);

        if (lookup.Status != HuggingFaceLookupStatus.Found || lookup.Revision is null || variant.Files.Count == 0
            || variant.Files.Any(file => string.IsNullOrWhiteSpace(file.Sha256) || file.Bytes <= 0))
        {
            return Task.FromResult(new LocalModelAddResult(null, "Look up the repository again, then choose a model to add."));
        }

        List<LocalModelFile> files = [.. variant.Files.Select(file => new LocalModelFile(file.Path, file.Bytes, file.Sha256))];

        lock (_userModelsLock)
        {
            LocalModelDefinition? existing = _userModels.FirstOrDefault(model => string.Equals(model.Repository, lookup.RepoId, StringComparison.OrdinalIgnoreCase)
                && model.Files.Select(file => file.Name).SequenceEqual(files.Select(file => file.Name), StringComparer.Ordinal));

            if (existing is not null)
            {
                return Task.FromResult(new LocalModelAddResult(existing.Id, null));
            }

            string name = lookup.RepoId[(lookup.RepoId.IndexOf('/') + 1)..];
            string displayName = GgufSuffix().Replace(name, string.Empty);
            LocalModelDefinition model = new(
                ModelId(lookup.RepoId, files[0].Name),
                $"{displayName} · {variant.Label}",
                $"From {lookup.RepoId} on Hugging Face. Runs with llama.cpp.",
                lookup.License ?? "See the repository",
                0,
                $"{HuggingFaceClient.SITE}{lookup.RepoId}/resolve/{lookup.Revision}/",
                files,
                LocalModelFormat.Gguf,
                lookup.RepoId);

            // Save first, so a failed save leaves nothing behind that a retry would take as added.
            SaveUserModels([.. _userModels, model]);
            _userModels.Add(model);
            _logger.LogInformation("Added local model {ModelId} from {Repository}.", model.Id, lookup.RepoId);

            return Task.FromResult(new LocalModelAddResult(model.Id, null));
        }
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
            FileInfo info = new(Path.Combine(directory, file.LocalName));
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

        // Loaded model files are locked on Windows, so delete them while the host holds the model unloaded; a reply
        // queued meanwhile then finds the files gone rather than reloading them.
        await Task.Run(() => model.Format == LocalModelFormat.Gguf && _llamaHost is not null
            ? _llamaHost.UnloadAsync(directory, DeleteFiles)
            : _host.UnloadAsync(directory, DeleteFiles), cancellationToken);

        bool forgotten = false;

        if (model.IsUserAdded)
        {
            lock (_userModelsLock)
            {
                if (_userModels.Contains(model))
                {
                    SaveUserModels([.. _userModels.Where(candidate => candidate != model)]);
                    forgotten = _userModels.Remove(model);
                }
            }
        }

        return existed || forgotten;
    }

    private List<LocalModelDefinition> LoadUserModels()
    {
        string path = Path.Combine(_modelsDirectory, USER_MODELS_FILE);

        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<LocalModelDefinition>>(File.ReadAllText(path), JSON) ?? [] : [];
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(error, "Could not read the added local models from {Path}.", path);
            return [];
        }
    }

    private void SaveUserModels(IReadOnlyList<LocalModelDefinition> models)
    {
        Directory.CreateDirectory(_modelsDirectory);
        string path = Path.Combine(_modelsDirectory, USER_MODELS_FILE);
        string temporary = path + ".tmp";

        // Write a new file and replace the old one, so an interrupted save cannot leave a half-written list.
        File.WriteAllText(temporary, JsonSerializer.Serialize(models, JSON));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// The model name for a file in a repository: the file's name, in lower case letters, digits, dots, dashes, and
    /// underscores, then a short fingerprint of the repository and the file's path in it. The same file always gets the
    /// same name, so a contact's assignment never moves to another model that happens to share a file name, whether in
    /// another repository or another folder of the same one.
    /// </summary>
    internal static string ModelId(string repository, string path)
    {
        string name = UnsafeIdCharacters().Replace(Path.GetFileNameWithoutExtension(path).ToLowerInvariant(), "-").Trim('-');
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{repository.ToLowerInvariant()}/{path}")))[..8].ToLowerInvariant();

        return $"{(name.Length == 0 ? "model" : name)}-{fingerprint}";
    }

    [GeneratedRegex("[^a-z0-9._-]+")]
    private static partial Regex UnsafeIdCharacters();

    [GeneratedRegex("[-_ ]?GGUF$", RegexOptions.IgnoreCase)]
    private static partial Regex GgufSuffix();

    private async Task<LocalModelDownloadResult> DownloadCoreAsync(LocalModelDefinition model, IProgress<LocalModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        string directory = ModelDirectory(model);
        string partial = directory + PARTIAL_SUFFIX;
        Directory.CreateDirectory(partial);

        long downloaded = model.Files.Sum(file => Math.Min(FileDownload.ExistingLength(Path.Combine(partial, file.LocalName)), file.Size));

        if (FileDownload.MissingSpace(partial, model.DownloadBytes - downloaded, model.DisplayName) is { } problem)
        {
            return LocalModelDownloadResult.Failed(problem);
        }

        DownloadProgress reporter = new(progress is null ? null : (bytes, total) => progress.Report(new LocalModelDownloadProgress(bytes, total)), model.DownloadBytes, downloaded);
        HttpClient client = _httpClientFactory.CreateClient();

        // A gated repository needs the user's token; the built-in models are public.
        AuthenticationHeaderValue? authorization = model.IsUserAdded ? await _huggingFace.AuthorizationAsync(cancellationToken) : null;

        foreach (LocalModelFile file in model.Files)
        {
            await FileDownload.DownloadAsync(client, new Uri(model.BaseUrl + file.UrlPath), Path.Combine(partial, file.LocalName), file.Size, reporter, cancellationToken, authorization);
        }

        reporter.Report(force: true);

        foreach (LocalModelFile file in model.Files)
        {
            string path = Path.Combine(partial, file.LocalName);

            if (!string.Equals(await FileDownload.HashAsync(path, cancellationToken), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                return LocalModelDownloadResult.Failed($"{file.LocalName} did not match its published checksum and was discarded. Try again to download it.");
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
