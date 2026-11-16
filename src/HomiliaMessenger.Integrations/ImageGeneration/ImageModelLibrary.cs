using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Credentials;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Integrations.LocalModels;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.ImageGeneration;

/// <summary>
/// Downloads image models into their own folder, apart from conversational and embedding models, and remembers which one
/// photos are generated with: the built-in catalog, and Stable Diffusion 1.x GGUF files the user adds from Hugging Face.
/// </summary>
/// <remarks>
/// Downloads work as they do for conversational models (see <see cref="LocalModelLibrary"/>): resumable, checked against
/// their published SHA-256 hashes, and moved into place only once complete. The chosen model's ID is kept in
/// <c>selected-model.txt</c> and added models in <c>user-models.json</c>, beside the model folders.
/// </remarks>
internal sealed class ImageModelLibrary : IImageModelLibrary
{
    private const string SELECTED_FILE = "selected-model.txt";
    private const string USER_MODELS_FILE = "user-models.json";

    /// <summary>
    /// How much of a file to read first when inspecting its header; most image checkpoints' headers are about 120 KB.
    /// </summary>
    internal const int FIRST_HEADER_READ = 512 * 1024;

    /// <summary>
    /// The most of a file read to inspect its header; a longer header is not a Stable Diffusion checkpoint's.
    /// </summary>
    internal const int MAX_HEADER_READ = 16 * 1024 * 1024;

    /// <summary>
    /// The most GGUF files inspected in one repository, so a large repository does not make hundreds of requests.
    /// </summary>
    internal const int MAX_FILES_INSPECTED = 24;

    private static readonly JsonSerializerOptions JSON = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly IReadOnlyList<ImageModelDefinition> _catalog;
    private readonly List<ImageModelDefinition> _userModels;
    private readonly string _directory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HuggingFaceClient _huggingFace;
    private readonly ImageGenerationGate _gate;
    private readonly ILogger<ImageModelLibrary> _logger;
    private readonly ConcurrentDictionary<string, bool> _downloading = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private string? _selectedId;

    public ImageModelLibrary(string directory, IHttpClientFactory httpClientFactory, ICredentialStore credentials, ImageGenerationGate gate, ILogger<ImageModelLibrary> logger)
        : this(ImageModelCatalog.Models, directory, httpClientFactory, gate, logger, credentials)
    {
    }

    internal ImageModelLibrary(IReadOnlyList<ImageModelDefinition> catalog, string directory, IHttpClientFactory httpClientFactory, ImageGenerationGate gate, ILogger<ImageModelLibrary> logger, ICredentialStore? credentials = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _huggingFace = new HuggingFaceClient(httpClientFactory, credentials);
        _userModels = LoadUserModels();
        _selectedId = LoadSelection();
    }

    /// <summary>
    /// Every model offered: the built-in ones, then those the user added.
    /// </summary>
    internal IReadOnlyList<ImageModelDefinition> Catalog
    {
        get
        {
            lock (_lock)
            {
                return [.. _catalog, .. _userModels];
            }
        }
    }

    /// <summary>
    /// The model photos are generated with: the chosen one when it is downloaded, otherwise the first downloaded model,
    /// so a single downloaded model is used without being chosen. <see langword="null"/> when none is downloaded.
    /// </summary>
    internal ImageModelDefinition? Active
    {
        get
        {
            IReadOnlyList<ImageModelDefinition> catalog = Catalog;
            ImageModelDefinition? selected = Find(_selectedId);

            return selected is not null && IsDownloaded(selected) ? selected : catalog.FirstOrDefault(IsDownloaded);
        }
    }

    public IReadOnlyList<ImageModel> List()
    {
        string? active = Active?.Id;

        return [.. Catalog.Select(model => new ImageModel(
            model.Id, model.DisplayName, model.Description, model.License, model.LicenseUrl, model.DownloadBytes, IsDownloaded(model),
            string.Equals(model.Id, active, StringComparison.OrdinalIgnoreCase), model.IsUserAdded, model.IsVerified, model.Settings))];
    }

    internal ImageModelDefinition? Find(string? id)
    {
        return Catalog.FirstOrDefault(model => string.Equals(model.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    internal string ModelDirectory(ImageModelDefinition model)
    {
        return Path.Combine(_directory, model.Id);
    }

    internal string CheckpointPath(ImageModelDefinition model)
    {
        return Path.Combine(ModelDirectory(model), model.Checkpoint.LocalName);
    }

    /// <summary>
    /// Whether every file is present at its expected size. Hashes are checked once, when the download completes.
    /// </summary>
    internal bool IsDownloaded(ImageModelDefinition model)
    {
        string directory = ModelDirectory(model);

        return model.Files.All(file =>
        {
            FileInfo info = new(Path.Combine(directory, file.LocalName));
            return info.Exists && info.Length == file.Size;
        });
    }

    public bool Select(string modelId)
    {
        ImageModelDefinition? model = Find(modelId);

        if (model is null)
        {
            return false;
        }

        lock (_lock)
        {
            SaveText(SELECTED_FILE, model.Id);
            _selectedId = model.Id;
        }

        _logger.LogInformation("Photos are now generated with image model {ModelId}.", model.Id);

        return true;
    }

    public async Task<LocalModelDownloadResult> DownloadAsync(string modelId, IProgress<LocalModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ImageModelDefinition? model = Find(modelId);

        if (model is null)
        {
            return LocalModelDownloadResult.Failed($"There is no image model named \"{modelId}\".");
        }

        if (!IsDownloaded(model))
        {
            if (!_downloading.TryAdd(model.Id, true))
            {
                return LocalModelDownloadResult.Failed($"{model.DisplayName} is already downloading.");
            }

            try
            {
                KeepCompleteFiles(model);
                AuthenticationHeaderValue? authorization = model.IsUserAdded ? await _huggingFace.AuthorizationAsync(cancellationToken) : null;
                LocalModelDownloadResult result = await LocalModelLibrary.DownloadFilesAsync(ModelDirectory(model), model.BaseUrl, model.Files, model.DisplayName, _httpClientFactory.CreateClient(), authorization, progress, cancellationToken);

                if (result.Status != LocalModelDownloadStatus.Success)
                {
                    return result;
                }

                _logger.LogInformation("Downloaded image model {ModelId}.", model.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return LocalModelDownloadResult.Canceled;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException)
            {
                _logger.LogWarning(error, "Downloading image model {ModelId} failed.", model.Id);
                return LocalModelDownloadResult.Failed($"Could not download {model.DisplayName}: {error.Message} Try again to resume.");
            }
            finally
            {
                _downloading.TryRemove(model.Id, out _);
            }
        }

        // Ready to use at once: chosen unless another downloaded model already is.
        if (Find(_selectedId) is not { } selected || !IsDownloaded(selected))
        {
            Select(model.Id);
        }

        return LocalModelDownloadResult.Success;
    }

    public async Task<bool> DeleteAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ImageModelDefinition? model = Find(modelId);

        if (model is null)
        {
            return false;
        }

        // Holding the download slot keeps a download from starting meanwhile; one already running must be stopped
        // first, or it would move its files into place after they were removed (and after the model was forgotten).
        if (!_downloading.TryAdd(model.Id, true))
        {
            _logger.LogInformation("Image model {ModelId} is downloading, so it was not removed.", model.Id);
            return false;
        }

        try
        {
            string directory = ModelDirectory(model);
            string partial = directory + LocalModelLibrary.PARTIAL_SUFFIX;
            bool existed = Directory.Exists(directory);

            // A model being drawn with has its file open, so wait for the drawing to finish.
            await _gate.RunExclusiveAsync(() =>
            {
                if (existed)
                {
                    Directory.Delete(directory, recursive: true);
                }

                if (Directory.Exists(partial))
                {
                    Directory.Delete(partial, recursive: true);
                }
            }, cancellationToken);

            bool forgotten = false;

            if (model.IsUserAdded)
            {
                lock (_lock)
                {
                    if (_userModels.Contains(model))
                    {
                        SaveUserModels([.. _userModels.Where(candidate => candidate != model)]);
                        forgotten = _userModels.Remove(model);
                    }
                }
            }

            _logger.LogInformation("Removed image model {ModelId}.", model.Id);

            return existed || forgotten;
        }
        finally
        {
            _downloading.TryRemove(model.Id, out _);
        }
    }

    public async Task<ImageModelLookup> LookUpAsync(string repository, CancellationToken cancellationToken = default)
    {
        (HuggingFaceRepository? found, HuggingFaceLookup? problem) = await _huggingFace.GetRepositoryAsync(repository, cancellationToken);

        if (found is null)
        {
            return ImageModelLookup.Unsuccessful(problem!.Status, problem.RepoId, problem.Problem ?? "Could not read the repository.");
        }

        List<HuggingFaceFile> ggufs = [.. found.Files
            .Where(file => file.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Bytes)];

        if (ggufs.Count == 0)
        {
            return new ImageModelLookup(HuggingFaceLookupStatus.NoModels, found.RepoId, found.Revision, found.License, found.PipelineTag, [],
                $"{found.RepoId} has no GGUF files. This app runs image models converted to GGUF for stable-diffusion.cpp; search Hugging Face for the model's name with \"GGUF\".");
        }

        List<ImageModelFile> files = [];

        foreach (HuggingFaceFile file in ggufs.Take(MAX_FILES_INSPECTED))
        {
            files.Add(await InspectAsync(found, file, cancellationToken));
        }

        string? note = ggufs.Count > MAX_FILES_INSPECTED ? $"Only the {MAX_FILES_INSPECTED} smallest of its {ggufs.Count} GGUF files were checked." : null;

        return new ImageModelLookup(HuggingFaceLookupStatus.Found, found.RepoId, found.Revision, found.License, found.PipelineTag, files, note);
    }

    /// <summary>
    /// Reads a file's header, a little at a time, to tell what model it holds.
    /// </summary>
    private async Task<ImageModelFile> InspectAsync(HuggingFaceRepository repository, HuggingFaceFile file, CancellationToken cancellationToken)
    {
        string? known = _catalog.FirstOrDefault(model => string.Equals(model.Checkpoint.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))?.Id;

        try
        {
            int bytes = (int)Math.Min(FIRST_HEADER_READ, file.Bytes);

            while (true)
            {
                byte[] header = await _huggingFace.ReadStartAsync(repository.RepoId, repository.Revision, file.Path, bytes, cancellationToken);
                GgufInspection inspection = GgufImageInspector.Inspect(header);

                if (inspection.NeedsBytes > 0 && header.Length == bytes && bytes < Math.Min(MAX_HEADER_READ, file.Bytes))
                {
                    bytes = (int)Math.Min(Math.Min(MAX_HEADER_READ, file.Bytes), Math.Max(inspection.NeedsBytes, (long)bytes * 4));
                    continue;
                }

                if (inspection.NeedsBytes > 0)
                {
                    inspection = GgufInspection.Unrecognized("The file's header is longer than any Stable Diffusion checkpoint's, or the file is cut short.");
                }

                return new ImageModelFile(file, inspection.Family, inspection.HasTextEncoder, inspection.HasVae, inspection.Quantization, inspection.Problem, known);
            }
        }
        catch (Exception error) when (error is HttpRequestException or IOException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new ImageModelFile(file, ImageModelFamily.Unknown, false, false, null, "Could not read the file's header from Hugging Face: " + error.Message, known);
        }
    }

    public Task<LocalModelAddResult> AddAsync(ImageModelLookup lookup, ImageModelFile file, ImageGenerationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(settings);

        if (file.KnownModelId is { } known)
        {
            return Task.FromResult(new LocalModelAddResult(known, null));
        }

        if (lookup.Status != HuggingFaceLookupStatus.Found || lookup.Revision is null || !lookup.Files.Contains(file)
            || string.IsNullOrWhiteSpace(file.File.Sha256) || file.File.Bytes <= 0)
        {
            return Task.FromResult(new LocalModelAddResult(null, "Look up the repository again, then choose a file to add."));
        }

        if (file.Problem is { } problem)
        {
            return Task.FromResult(new LocalModelAddResult(null, problem));
        }

        if (settings.Problem() is { } settingsProblem)
        {
            return Task.FromResult(new LocalModelAddResult(null, settingsProblem));
        }

        LocalModelFile checkpoint = new(file.File.Path, file.File.Bytes, file.File.Sha256);

        lock (_lock)
        {
            ImageModelDefinition? existing = _userModels.FirstOrDefault(model => string.Equals(model.Repository, lookup.RepoId, StringComparison.OrdinalIgnoreCase)
                && model.Checkpoint.Name == checkpoint.Name);

            if (existing is not null)
            {
                return Task.FromResult(new LocalModelAddResult(existing.Id, null));
            }

            ImageModelDefinition model = new(
                LocalModelLibrary.ModelId(lookup.RepoId, checkpoint.Name),
                Path.GetFileNameWithoutExtension(checkpoint.LocalName) + (file.Quantization is { } quantization ? $" · {quantization}" : string.Empty),
                $"From {lookup.RepoId} on Hugging Face. A Stable Diffusion 1.x model whose compatibility with this app has not been verified.",
                lookup.License ?? "See the repository",
                lookup.Url,
                $"{HuggingFaceClient.SITE}{lookup.RepoId}/resolve/{lookup.Revision}/",
                [checkpoint],
                settings,
                false,
                lookup.RepoId);

            // Save first, so a failed save leaves nothing behind that a retry would take as added.
            SaveUserModels([.. _userModels, model]);
            _userModels.Add(model);
            _logger.LogInformation("Added image model {ModelId} from {Repository}.", model.Id, lookup.RepoId);

            return Task.FromResult(new LocalModelAddResult(model.Id, null));
        }
    }

    /// <summary>
    /// Moves files already in place at their expected size into the download folder, so restoring a missing file (such
    /// as a deleted license) does not download the whole model again. They are checked against their hashes with the rest.
    /// </summary>
    private void KeepCompleteFiles(ImageModelDefinition model)
    {
        string directory = ModelDirectory(model);
        string partial = directory + LocalModelLibrary.PARTIAL_SUFFIX;

        if (!Directory.Exists(directory))
        {
            return;
        }

        Directory.CreateDirectory(partial);

        foreach (LocalModelFile file in model.Files)
        {
            string source = Path.Combine(directory, file.LocalName);
            string target = Path.Combine(partial, file.LocalName);

            if (File.Exists(source) && !File.Exists(target) && new FileInfo(source).Length == file.Size)
            {
                File.Move(source, target);
            }
        }
    }

    private List<ImageModelDefinition> LoadUserModels()
    {
        string path = Path.Combine(_directory, USER_MODELS_FILE);

        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<ImageModelDefinition>>(File.ReadAllText(path), JSON) ?? [] : [];
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(error, "Could not read the added image models from {Path}.", path);
            return [];
        }
    }

    private void SaveUserModels(IReadOnlyList<ImageModelDefinition> models)
    {
        SaveText(USER_MODELS_FILE, JsonSerializer.Serialize(models, JSON));
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
            _logger.LogWarning(error, "Could not read the chosen image model from {Path}.", path);
            return null;
        }
    }

    /// <summary>
    /// Writes a new file and replaces the old one, so an interrupted save cannot leave a half-written file.
    /// </summary>
    private void SaveText(string name, string text)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, name);
        string temporary = path + ".tmp";

        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }
}
