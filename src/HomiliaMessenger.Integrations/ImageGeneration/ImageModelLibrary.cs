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
/// photos are generated with: the built-in catalog, and GGUF files the user adds from Hugging Face (Stable Diffusion 1.x
/// for the processor; also SDXL and FLUX.2 [klein] 4B for a graphics card).
/// </summary>
/// <remarks>
/// Downloads work as they do for conversational models (see <see cref="LocalModelLibrary"/>): resumable, checked against
/// their published SHA-256 hashes, and moved into place only once complete. The model chosen for contact photos is kept
/// in <c>selected-model.txt</c>, the one for selfies in <c>selected-selfie-model.txt</c>, and added models in
/// <c>user-models.json</c>, beside the model folders. Files several models share, such as FLUX.2 [klein]'s text encoder
/// and VAE, are kept once each under <c>components</c>, and removed with the last downloaded model that uses them. Face
/// matching, which Stable Diffusion selfie models need, has a folder of its own, with an adapter for each family and one
/// image encoder, converted to Q8_0 after downloading and the downloaded file removed.
/// </remarks>
internal sealed class ImageModelLibrary : IImageModelLibrary
{
    private const string SELECTED_FILE = "selected-model.txt";
    private const string SELECTED_SELFIE_FILE = "selected-selfie-model.txt";
    private const string USER_MODELS_FILE = "user-models.json";
    internal const string COMPONENTS_DIRECTORY = "components";

    /// <summary>
    /// The ID under which face matching downloads, so it cannot download twice at once or alongside its removal.
    /// </summary>
    private const string FACE_MATCHING_KEY = "\u0000face-matching";

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
    private readonly IStableDiffusionBackend? _backend;
    private readonly FaceMatchingDefinition _faceMatching;
    private readonly ConcurrentDictionary<string, bool> _downloading = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _modelsDownloading = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private string? _selectedId;
    private string? _selectedSelfieId;

    public ImageModelLibrary(string directory, IHttpClientFactory httpClientFactory, ICredentialStore credentials, ImageGenerationGate gate, IStableDiffusionBackend backend, ILogger<ImageModelLibrary> logger)
        : this(ImageModelCatalog.Models, directory, httpClientFactory, gate, logger, credentials, backend)
    {
    }

    /// <param name="backend">The runtime that prepares face matching's encoder; without it, face matching cannot be downloaded.</param>
    /// <param name="faceMatching">Face matching's files; the catalog's unless given.</param>
    internal ImageModelLibrary(IReadOnlyList<ImageModelDefinition> catalog, string directory, IHttpClientFactory httpClientFactory, ImageGenerationGate gate, ILogger<ImageModelLibrary> logger, ICredentialStore? credentials = null, IStableDiffusionBackend? backend = null, FaceMatchingDefinition? faceMatching = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _backend = backend;
        _faceMatching = faceMatching ?? FaceMatchingCatalog.Definition;
        _huggingFace = new HuggingFaceClient(httpClientFactory, credentials);
        _userModels = LoadUserModels();
        _selectedId = LoadSelection(SELECTED_FILE);
        _selectedSelfieId = LoadSelection(SELECTED_SELFIE_FILE);
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
    /// The model contact photos are generated with (see <see cref="ActiveFor"/>).
    /// </summary>
    internal ImageModelDefinition? Active => ActiveFor(ImageModelPurpose.Portraits);

    /// <summary>
    /// The model images for <paramref name="purpose"/> are generated with: the chosen one when it is downloaded, otherwise
    /// the first downloaded model offered for the purpose, so a single downloaded model is used without being chosen. So a
    /// model already downloaded for contact photos is used for selfies too, once face matching is. <see langword="null"/>
    /// when none is downloaded.
    /// </summary>
    internal ImageModelDefinition? ActiveFor(ImageModelPurpose purpose)
    {
        IReadOnlyList<ImageModelDefinition> offered = [.. Catalog.Where(model => model.IsOffered(purpose))];
        ImageModelDefinition? selected = offered.FirstOrDefault(model => string.Equals(model.Id, SelectedId(purpose), StringComparison.OrdinalIgnoreCase));

        return selected is not null && IsDownloaded(selected) ? selected : offered.FirstOrDefault(IsDownloaded);
    }

    public IReadOnlyList<ImageModel> List(ImageModelPurpose purpose = ImageModelPurpose.Portraits)
    {
        string? active = ActiveFor(purpose)?.Id;
        bool selfies = purpose == ImageModelPurpose.Selfies;

        return [.. Catalog.Where(model => model.IsOffered(purpose)).Select(model =>
        {
            bool faceMatching = selfies && model.FaceSource == SelfieFaceSource.FaceMatching;

            return new ImageModel(
                model.Id,
                model.DisplayName,
                selfies ? model.SelfieDescription ?? model.Description : model.Description,
                faceMatching ? $"{model.License} · {FaceMatchingCatalog.LICENSE}" : model.License,
                model.LicenseUrl,
                model.DownloadBytes + (faceMatching ? FaceMatchingDownloadBytes(model.Family) : 0),
                selfies ? IsSelfieReady(model) : IsDownloaded(model),
                string.Equals(model.Id, active, StringComparison.OrdinalIgnoreCase),
                model.IsUserAdded,
                model.IsVerified,
                model.SettingsFor(purpose),
                model.Purposes,
                model.Processor,
                model.Family,
                model.Style,
                model.FaceSource);
        })];
    }

    public FaceMatching GetFaceMatching()
    {
        return new FaceMatching(FaceMatchingCatalog.NAME, FaceMatchingCatalog.LICENSE, FaceMatchingCatalog.LICENSE_URL, _faceMatching.DownloadBytes, _faceMatching.DiskBytes, IsFaceMatchingReady);
    }

    internal string FaceMatchingDirectory => Path.Combine(_directory, FaceMatchingCatalog.DIRECTORY_NAME);

    private string PreparedEncoderPath => Path.Combine(FaceMatchingDirectory, FaceMatchingCatalog.PREPARED_ENCODER);

    /// <summary>
    /// Stable Diffusion 1.x face matching's files as the runtime opens them, whether or not they are there.
    /// </summary>
    internal FaceMatchingFiles FaceMatchingPaths => FaceMatchingPathsFor(ImageModelFamily.StableDiffusion1)!;

    /// <summary>
    /// Face matching's files for selfies of a model of <paramref name="family"/>, whether or not they are there, or
    /// <see langword="null"/> when there is no face matching for it.
    /// </summary>
    internal FaceMatchingFiles? FaceMatchingPathsFor(ImageModelFamily family)
    {
        return _faceMatching.AdapterFor(family) is { } adapter ? new(PreparedEncoderPath, Path.Combine(FaceMatchingDirectory, adapter.LocalName)) : null;
    }

    /// <summary>
    /// Whether Stable Diffusion 1.x face matching is downloaded and its encoder prepared, at their expected sizes.
    /// </summary>
    internal bool IsFaceMatchingReady => IsFaceMatchingReadyFor(ImageModelFamily.StableDiffusion1);

    /// <summary>
    /// Whether face matching for <paramref name="family"/> is downloaded and its encoder prepared, at their expected sizes.
    /// </summary>
    internal bool IsFaceMatchingReadyFor(ImageModelFamily family)
    {
        return _faceMatching.AdapterFor(family) is { } adapter && IsEncoderPrepared && HasFaceMatchingFile(adapter);
    }

    private bool IsEncoderPrepared => new FileInfo(PreparedEncoderPath) is { Exists: true } encoder && encoder.Length == _faceMatching.PreparedEncoderBytes;

    private bool HasFaceMatchingFile(LocalModelFile file)
    {
        return new FileInfo(Path.Combine(FaceMatchingDirectory, file.LocalName)) is { Exists: true } info && info.Length == file.Size;
    }

    /// <summary>
    /// What downloading face matching for <paramref name="family"/> takes in all: its adapter and the image encoder.
    /// </summary>
    private long FaceMatchingDownloadBytes(ImageModelFamily family)
    {
        return (_faceMatching.AdapterFor(family)?.Size ?? 0) + _faceMatching.ImageEncoder.Size;
    }

    /// <summary>
    /// Whether everything a selfie with <paramref name="model"/> needs is downloaded: the model, and face matching for it
    /// unless it keeps a face from a reference image.
    /// </summary>
    internal bool IsSelfieReady(ImageModelDefinition model)
    {
        return IsDownloaded(model) && (model.FaceSource == SelfieFaceSource.ReferenceImage || IsFaceMatchingReadyFor(model.Family));
    }

    /// <summary>
    /// Where a shared file is kept, whether or not it is there.
    /// </summary>
    internal string ComponentPath(ImageModelComponent component)
    {
        return Path.Combine(ComponentDirectory(component), component.File.LocalName);
    }

    private string ComponentDirectory(ImageModelComponent component)
    {
        return Path.Combine(_directory, COMPONENTS_DIRECTORY, component.Id);
    }

    internal bool IsComponentDownloaded(ImageModelComponent component)
    {
        return new FileInfo(ComponentPath(component)) is { Exists: true } info && info.Length == component.File.Size;
    }

    /// <summary>
    /// The files the runtime opens <paramref name="model"/> from: its checkpoint, or its diffusion model with the shared
    /// text encoder and VAE.
    /// </summary>
    internal ImageModelPaths PathsFor(ImageModelDefinition model)
    {
        if (!model.IsDiffusionModelOnly)
        {
            return new ImageModelPaths(CheckpointPath(model));
        }

        string? Shared(ImageComponentRole role)
        {
            return model.SharedFiles.FirstOrDefault(component => component.Role == role) is { } component ? ComponentPath(component) : null;
        }

        return new ImageModelPaths(null, CheckpointPath(model), Shared(ImageComponentRole.TextEncoder), Shared(ImageComponentRole.Vae));
    }

    private string? SelectedId(ImageModelPurpose purpose)
    {
        lock (_lock)
        {
            return purpose == ImageModelPurpose.Selfies ? _selectedSelfieId : _selectedId;
        }
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
    /// Whether every file, with the shared files the model needs, is present at its expected size. Hashes are checked once,
    /// when the download completes.
    /// </summary>
    internal bool IsDownloaded(ImageModelDefinition model)
    {
        return HasOwnFiles(model) && model.SharedFiles.All(IsComponentDownloaded);
    }

    private bool HasOwnFiles(ImageModelDefinition model)
    {
        string directory = ModelDirectory(model);

        return model.Files.All(file =>
        {
            FileInfo info = new(Path.Combine(directory, file.LocalName));
            return info.Exists && info.Length == file.Size;
        });
    }

    public bool Select(string modelId, ImageModelPurpose purpose = ImageModelPurpose.Portraits)
    {
        ImageModelDefinition? model = Find(modelId);

        if (model is null || !model.IsOffered(purpose))
        {
            return false;
        }

        lock (_lock)
        {
            if (purpose == ImageModelPurpose.Selfies)
            {
                SaveText(SELECTED_SELFIE_FILE, model.Id);
                _selectedSelfieId = model.Id;
            }
            else
            {
                SaveText(SELECTED_FILE, model.Id);
                _selectedId = model.Id;
            }
        }

        _logger.LogInformation("{Purpose} are now generated with image model {ModelId}.", purpose, model.Id);

        return true;
    }

    public async Task<LocalModelDownloadResult> DownloadAsync(string modelId, ImageModelPurpose purpose = ImageModelPurpose.Portraits, IProgress<LocalModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // Counted as using its shared files from the start, before it looks for them, so removing another model that
        // shares them keeps them.
        bool registered = _modelsDownloading.TryAdd(modelId, true);

        try
        {
            return await DownloadModelAndPartsAsync(modelId, purpose, progress, cancellationToken);
        }
        finally
        {
            if (registered)
            {
                _modelsDownloading.TryRemove(modelId, out _);
            }
        }
    }

    private async Task<LocalModelDownloadResult> DownloadModelAndPartsAsync(string modelId, ImageModelPurpose purpose, IProgress<LocalModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        ImageModelDefinition? model = Find(modelId);

        if (model is null || !model.IsOffered(purpose))
        {
            return LocalModelDownloadResult.Failed($"There is no image model named \"{modelId}\" for {(purpose == ImageModelPurpose.Selfies ? "selfies" : "contact photos")}.");
        }

        bool needsFaceMatching = purpose == ImageModelPurpose.Selfies && model.FaceSource == SelfieFaceSource.FaceMatching && !IsFaceMatchingReadyFor(model.Family);

        if (needsFaceMatching && _faceMatching.AdapterFor(model.Family) is null)
        {
            return LocalModelDownloadResult.Failed($"There is no face matching for {model.DisplayName}'s kind of model, so it cannot take selfies.");
        }

        // The shared files, the model, and face matching download one after the other, reported as one download.
        // A shared file being downloaded or removed for another model is not taken as present: it goes through
        // DownloadComponentAsync, which waits for neither and reports it busy.
        IReadOnlyList<ImageModelComponent> components = [.. model.SharedFiles.Where(component => _downloading.ContainsKey(ComponentKey(component)) || !IsComponentDownloaded(component))];
        bool needsModel = !HasOwnFiles(model);
        long componentBytes = components.Sum(component => component.File.Size);
        long modelBytes = needsModel ? model.Files.Sum(file => file.Size) : 0;
        long total = componentBytes + modelBytes + (needsFaceMatching ? FaceMatchingDownloadBytes(model.Family) : 0);
        long offset = 0;

        foreach (ImageModelComponent component in components)
        {
            IProgress<LocalModelDownloadProgress>? componentProgress = progress is null ? null : new OffsetProgress(progress, offset, total);
            LocalModelDownloadResult componentResult = await DownloadComponentAsync(component, componentProgress, cancellationToken);

            if (componentResult.Status != LocalModelDownloadStatus.Success)
            {
                return componentResult;
            }

            offset += component.File.Size;
        }

        if (needsModel)
        {
            IProgress<LocalModelDownloadProgress>? modelProgress = progress is null ? null : new OffsetProgress(progress, offset, total);
            LocalModelDownloadResult modelResult = await DownloadModelAsync(model, modelProgress, cancellationToken);

            if (modelResult.Status != LocalModelDownloadStatus.Success)
            {
                return modelResult;
            }

            offset += modelBytes;
        }

        if (needsFaceMatching)
        {
            IProgress<LocalModelDownloadProgress>? faceProgress = progress is null ? null : new OffsetProgress(progress, offset, total);
            LocalModelDownloadResult faceResult = await DownloadFaceMatchingAsync(model.Family, faceProgress, cancellationToken);

            if (faceResult.Status != LocalModelDownloadStatus.Success)
            {
                return faceResult;
            }
        }

        // A shared file removed with another model while this one downloaded its own files is fetched again next time.
        if (!IsDownloaded(model))
        {
            return LocalModelDownloadResult.Failed($"{model.DisplayName}'s shared files were removed while it downloaded. Try again.");
        }

        // Ready to use at once: chosen unless another downloaded model already is.
        if (Find(SelectedId(purpose)) is not { } selected || !selected.IsOffered(purpose) || !IsDownloaded(selected))
        {
            Select(model.Id, purpose);
        }

        return LocalModelDownloadResult.Success;
    }

    /// <summary>
    /// Downloads a file several models share, once.
    /// </summary>
    private async Task<LocalModelDownloadResult> DownloadComponentAsync(ImageModelComponent component, IProgress<LocalModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        string key = ComponentKey(component);

        // Not taken as present while it is being downloaded or removed for another model.
        if (!_downloading.ContainsKey(key) && IsComponentDownloaded(component))
        {
            return LocalModelDownloadResult.Success;
        }

        if (!_downloading.TryAdd(key, true))
        {
            return LocalModelDownloadResult.Failed($"{component.DisplayName} is already downloading for another model. Try again when it is done.");
        }

        try
        {
            LocalModelDownloadResult result = await LocalModelLibrary.DownloadFilesAsync(ComponentDirectory(component), component.BaseUrl, [component.File], component.DisplayName, _httpClientFactory.CreateClient(), null, progress, cancellationToken);

            if (result.Status == LocalModelDownloadStatus.Success)
            {
                _logger.LogInformation("Downloaded shared image model file {ComponentId}.", component.Id);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LocalModelDownloadResult.Canceled;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException)
        {
            _logger.LogWarning(error, "Downloading shared image model file {ComponentId} failed.", component.Id);
            return LocalModelDownloadResult.Failed($"Could not download {component.DisplayName}: {error.Message} Try again to resume.");
        }
        finally
        {
            _downloading.TryRemove(key, out _);
        }
    }

    private static string ComponentKey(ImageModelComponent component)
    {
        return "\u0000component:" + component.Id;
    }

    /// <summary>
    /// Downloads the model's own files.
    /// </summary>
    private async Task<LocalModelDownloadResult> DownloadModelAsync(ImageModelDefinition model, IProgress<LocalModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (!HasOwnFiles(model))
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

        return LocalModelDownloadResult.Success;
    }

    /// <summary>
    /// Downloads Stable Diffusion 1.x face matching (see <see cref="DownloadFaceMatchingAsync(ImageModelFamily, IProgress{LocalModelDownloadProgress}?, CancellationToken)"/>).
    /// </summary>
    internal Task<LocalModelDownloadResult> DownloadFaceMatchingAsync(IProgress<LocalModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        return DownloadFaceMatchingAsync(ImageModelFamily.StableDiffusion1, progress, cancellationToken);
    }

    /// <summary>
    /// Downloads face matching for <paramref name="family"/>, or finishes preparing it after an interruption: its adapter,
    /// and the image encoder every adapter shares, which is converted to Q8_0 with the integrated runtime and the
    /// downloaded encoder removed. Adapters already downloaded for other families are kept.
    /// </summary>
    internal async Task<LocalModelDownloadResult> DownloadFaceMatchingAsync(ImageModelFamily family, IProgress<LocalModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (IsFaceMatchingReadyFor(family))
        {
            return LocalModelDownloadResult.Success;
        }

        if (_faceMatching.AdapterFor(family) is not { } adapterFile)
        {
            return LocalModelDownloadResult.Failed("There is no face matching for this kind of model.");
        }

        if (_backend is null || _backend.RuntimeProblem is not null)
        {
            return LocalModelDownloadResult.Failed(_backend?.RuntimeProblem ?? "Face matching needs the image generation runtime, which this build does not include.");
        }

        if (!_downloading.TryAdd(FACE_MATCHING_KEY, true))
        {
            return LocalModelDownloadResult.Failed("Face matching is already downloading.");
        }

        string directory = FaceMatchingDirectory;
        string source = Path.Combine(directory, _faceMatching.ImageEncoder.LocalName);
        FaceMatchingFiles paths = FaceMatchingPathsFor(family)!;
        LocalModelFile preparedEncoder = new(FaceMatchingCatalog.PREPARED_ENCODER, _faceMatching.PreparedEncoderBytes, string.Empty);

        // Every face matching file already in place is kept while others download into the same folder.
        IReadOnlyList<LocalModelFile> kept = [.. _faceMatching.Adapters, preparedEncoder, _faceMatching.ImageEncoder];

        try
        {
            // With the encoder already prepared, only the adapter is missing: it alone is downloaded, into a folder of its
            // own, and moved in beside the installed files once complete, so an interrupted download leaves the face
            // matching already in place working.
            if (IsEncoderPrepared)
            {
                string staging = directory + "-" + Path.GetFileNameWithoutExtension(adapterFile.LocalName);
                LocalModelDownloadResult adapterResult = await LocalModelLibrary.DownloadFilesAsync(staging, _faceMatching.BaseUrl, [adapterFile], FaceMatchingCatalog.NAME, _httpClientFactory.CreateClient(), null, progress, cancellationToken);

                if (adapterResult.Status == LocalModelDownloadStatus.Success)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(paths.AdapterPath)!);
                    File.Move(Path.Combine(staging, adapterFile.LocalName), paths.AdapterPath, overwrite: true);
                    Directory.Delete(staging, recursive: true);
                }

                return adapterResult;
            }

            // A download that finished before its encoder was prepared (the app closed meanwhile) is prepared without
            // downloading again.
            bool downloaded = new FileInfo(source) is { Exists: true } encoder && encoder.Length == _faceMatching.ImageEncoder.Size
                && new FileInfo(paths.AdapterPath) is { Exists: true } adapter && adapter.Length == adapterFile.Size;

            if (!downloaded)
            {
                KeepCompleteFiles(directory, kept);
                LocalModelDownloadResult result = await LocalModelLibrary.DownloadFilesAsync(directory, _faceMatching.BaseUrl, [adapterFile, _faceMatching.ImageEncoder], FaceMatchingCatalog.NAME, _httpClientFactory.CreateClient(), null, progress, cancellationToken);

                if (result.Status != LocalModelDownloadStatus.Success)
                {
                    return result;
                }
            }

            // Converted to a temporary file and moved into place, so an interrupted conversion is never taken as done.
            string temporary = paths.ImageEncoderPath + ".tmp";
            await _gate.RunExclusiveAsync(() =>
            {
                File.Delete(temporary);
                _backend.ConvertToQ8(source, temporary);
            }, cancellationToken);

            // The runtime's conversion cannot be interrupted (it takes a few seconds); paused meanwhile, its result is
            // discarded and the downloaded encoder kept, so preparing resumes without downloading again.
            if (cancellationToken.IsCancellationRequested)
            {
                File.Delete(temporary);
                return LocalModelDownloadResult.Canceled;
            }

            if (new FileInfo(temporary).Length != _faceMatching.PreparedEncoderBytes)
            {
                File.Delete(temporary);
                return LocalModelDownloadResult.Failed("Face matching's image encoder was not prepared as expected. Try again.");
            }

            File.Move(temporary, paths.ImageEncoderPath, overwrite: true);
            File.Delete(source);
            _logger.LogInformation("Downloaded face matching and prepared its image encoder.");

            return LocalModelDownloadResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LocalModelDownloadResult.Canceled;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            _logger.LogWarning(error, "Downloading face matching failed.");
            return LocalModelDownloadResult.Failed($"Could not download face matching: {error.Message} Try again to resume.");
        }
        finally
        {
            _downloading.TryRemove(FACE_MATCHING_KEY, out _);
        }
    }

    public async Task<bool> DeleteAsync(string modelId, ImageModelPurpose purpose = ImageModelPurpose.Portraits, CancellationToken cancellationToken = default)
    {
        ImageModelDefinition? model = Find(modelId);

        if (model is null || !model.IsOffered(purpose))
        {
            return false;
        }

        // A model that also makes contact photos stays; what selfies alone need of it is face matching, and nothing when it
        // keeps a face from a reference image.
        if (purpose == ImageModelPurpose.Selfies && model.IsOffered(ImageModelPurpose.Portraits))
        {
            // Kept while another downloaded selfie model of the same family still matches faces with it.
            bool neededElsewhere = Catalog.Any(other => other.Id != model.Id && other.Family == model.Family && other.FaceSource == SelfieFaceSource.FaceMatching
                && other.IsOffered(ImageModelPurpose.Selfies) && IsDownloaded(other));

            return model.FaceSource == SelfieFaceSource.FaceMatching && !neededElsewhere && await DeleteFaceMatchingAsync(model.Family, cancellationToken);
        }

        // A model the user added for both is taken off this purpose's list and stays downloaded for the other, which
        // still draws with its checkpoint.
        ImageModelPurpose remaining = model.Purposes & ~purpose;

        if (model.IsUserAdded && remaining != ImageModelPurpose.None)
        {
            return Forget(model, model with { Purposes = remaining });
        }

        if (await DeleteFilesAsync(model, cancellationToken) is not { } existed)
        {
            return false;
        }

        bool forgotten = model.IsUserAdded && Forget(model, null);
        await DeleteUnusedComponentsAsync(model, cancellationToken);

        return existed | forgotten;
    }

    /// <summary>
    /// Removes the shared files of a removed model that no other downloaded model uses.
    /// </summary>
    private async Task DeleteUnusedComponentsAsync(ImageModelDefinition removed, CancellationToken cancellationToken)
    {
        foreach (ImageModelComponent component in removed.SharedFiles)
        {
            // Reserved before its users are counted, so a download that starts meanwhile either is counted or waits for
            // the removal and downloads it again.
            string key = ComponentKey(component);

            if (!_downloading.TryAdd(key, true))
            {
                continue;
            }

            try
            {
                // Kept for a model that has its own files or is downloading.
                // A download of the removed model itself, started again since, counts too.
                bool used = Catalog.Any(model => ((model.Id != removed.Id && HasOwnFiles(model)) || _modelsDownloading.ContainsKey(model.Id))
                    && model.SharedFiles.Any(shared => shared.Id == component.Id));

                if (used)
                {
                    continue;
                }

                string directory = ComponentDirectory(component);
                string partial = directory + LocalModelLibrary.PARTIAL_SUFFIX;

                await _gate.RunExclusiveAsync(() =>
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }

                    if (Directory.Exists(partial))
                    {
                        Directory.Delete(partial, recursive: true);
                    }
                }, cancellationToken);

                _logger.LogInformation("Removed shared image model file {ComponentId}, which no downloaded model uses.", component.Id);
            }
            finally
            {
                _downloading.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Removes face matching for <paramref name="family"/>: its adapter, and the whole folder with the shared image encoder
    /// when no other family's adapter is downloaded.
    /// </summary>
    private async Task<bool> DeleteFaceMatchingAsync(ImageModelFamily family, CancellationToken cancellationToken)
    {
        if (_faceMatching.AdapterFor(family) is not { } adapter)
        {
            return false;
        }

        if (_faceMatching.Adapters.Any(other => other != adapter && HasFaceMatchingFile(other)))
        {
            if (!_downloading.TryAdd(FACE_MATCHING_KEY, true))
            {
                return false;
            }

            try
            {
                string path = Path.Combine(FaceMatchingDirectory, adapter.LocalName);
                bool existed = File.Exists(path);
                await _gate.RunExclusiveAsync(() => File.Delete(path), cancellationToken);
                _logger.LogInformation("Removed face matching for {Family}.", family);

                return existed;
            }
            finally
            {
                _downloading.TryRemove(FACE_MATCHING_KEY, out _);
            }
        }

        return await DeleteFaceMatchingAsync(cancellationToken);
    }

    /// <summary>
    /// Removes face matching, with every adapter and the image encoder.
    /// </summary>
    private async Task<bool> DeleteFaceMatchingAsync(CancellationToken cancellationToken)
    {
        if (!_downloading.TryAdd(FACE_MATCHING_KEY, true))
        {
            _logger.LogInformation("Face matching is downloading, so it was not removed.");
            return false;
        }

        try
        {
            string directory = FaceMatchingDirectory;
            string partial = directory + LocalModelLibrary.PARTIAL_SUFFIX;
            bool existed = Directory.Exists(directory);

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

            _logger.LogInformation("Removed face matching.");

            return existed;
        }
        finally
        {
            _downloading.TryRemove(FACE_MATCHING_KEY, out _);
        }
    }

    /// <summary>
    /// Replaces or removes an added model in the list kept on disk. Returns whether it was there.
    /// </summary>
    private bool Forget(ImageModelDefinition model, ImageModelDefinition? replacement)
    {
        lock (_lock)
        {
            if (!_userModels.Contains(model))
            {
                return false;
            }

            List<ImageModelDefinition> updated = [.. _userModels.Select(candidate => candidate == model ? replacement : candidate).OfType<ImageModelDefinition>()];
            SaveUserModels(updated);
            _userModels.Clear();
            _userModels.AddRange(updated);

            return true;
        }
    }

    /// <summary>
    /// Removes a model's files. Returns whether there were any, or <see langword="null"/> when the model is downloading and
    /// nothing was removed.
    /// </summary>
    private async Task<bool?> DeleteFilesAsync(ImageModelDefinition model, CancellationToken cancellationToken)
    {
        // Holding the download slot keeps a download from starting meanwhile; one already running must be stopped
        // first, or it would move its files into place after they were removed (and after the model was forgotten).
        if (!_downloading.TryAdd(model.Id, true))
        {
            _logger.LogInformation("Image model {ModelId} is downloading, so it was not removed.", model.Id);
            return null;
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

            _logger.LogInformation("Removed image model {ModelId}.", model.Id);

            return existed;
        }
        finally
        {
            _downloading.TryRemove(model.Id, out _);
        }
    }

    public async Task<ImageModelLookup> LookUpAsync(string repository, ImageProcessor processor = ImageProcessor.Cpu, CancellationToken cancellationToken = default)
    {
        (HuggingFaceRepository? found, HuggingFaceLookup? problem) = await _huggingFace.GetRepositoryAsync(repository, cancellationToken);

        if (found is null)
        {
            return ImageModelLookup.Unsuccessful(problem!.Status, problem.RepoId, problem.Problem ?? "Could not read the repository.", processor);
        }

        List<HuggingFaceFile> ggufs = [.. found.Files
            .Where(file => file.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Bytes)];

        if (ggufs.Count == 0)
        {
            return new ImageModelLookup(HuggingFaceLookupStatus.NoModels, found.RepoId, found.Revision, found.License, found.PipelineTag, [],
                $"{found.RepoId} has no GGUF files. This app runs image models converted to GGUF for stable-diffusion.cpp; search Hugging Face for the model's name with \"GGUF\".", processor);
        }

        List<ImageModelFile> files = [];

        foreach (HuggingFaceFile file in ggufs.Take(MAX_FILES_INSPECTED))
        {
            ImageModelFile inspected = await InspectAsync(found, file, cancellationToken);
            files.Add(inspected with { Problem = inspected.Problem ?? ProcessorProblem(inspected.Family, processor) });
        }

        string? note = ggufs.Count > MAX_FILES_INSPECTED ? $"Only the {MAX_FILES_INSPECTED} smallest of its {ggufs.Count} GGUF files were checked." : null;

        return new ImageModelLookup(HuggingFaceLookupStatus.Found, found.RepoId, found.Revision, found.License, found.PipelineTag, files, note, processor);
    }

    /// <summary>
    /// Why a model of <paramref name="family"/> cannot run on <paramref name="processor"/>, or <see langword="null"/>.
    /// </summary>
    internal static string? ProcessorProblem(ImageModelFamily family, ImageProcessor processor)
    {
        return family switch
        {
            ImageModelFamily.StableDiffusion1 => null,
            ImageModelFamily.StableDiffusionXL or ImageModelFamily.Flux2Klein4B when processor == ImageProcessor.Gpu => null,
            ImageModelFamily.StableDiffusionXL => "This is an SDXL model, which this app runs on a graphics card only. Add it under Graphics card.",
            ImageModelFamily.Flux2Klein4B => "This is a FLUX.2 [klein] 4B model, which this app runs on a graphics card only. Add it under Graphics card.",
            _ => GgufImageInspector.SUPPORTED
        };
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
                    inspection = GgufInspection.Unrecognized("The file's header is longer than any image model's this app runs, or the file is cut short.");
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

    public Task<LocalModelAddResult> AddAsync(ImageModelLookup lookup, ImageModelFile file, ImageGenerationSettings settings, ImageModelPurpose purpose = ImageModelPurpose.Portraits, ImagePromptStyle? promptStyle = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(settings);

        if (purpose is not (ImageModelPurpose.Portraits or ImageModelPurpose.Selfies))
        {
            return Task.FromResult(new LocalModelAddResult(null, "Add a model for contact photos or for selfies."));
        }

        if (file.KnownModelId is { } known)
        {
            return Task.FromResult(new LocalModelAddResult(known, null));
        }

        if (lookup.Status != HuggingFaceLookupStatus.Found || lookup.Revision is null || !lookup.Files.Contains(file)
            || string.IsNullOrWhiteSpace(file.File.Sha256) || file.File.Bytes <= 0)
        {
            return Task.FromResult(new LocalModelAddResult(null, "Look up the repository again, then choose a file to add."));
        }

        if ((file.Problem ?? ProcessorProblem(file.Family, lookup.Processor)) is { } problem)
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

            bool selfies = purpose == ImageModelPurpose.Selfies;

            if (existing is not null && existing.Processor != lookup.Processor)
            {
                string where = existing.Processor == ImageProcessor.Gpu ? "graphics card" : "processor";
                return Task.FromResult(new LocalModelAddResult(null, $"This model was already added to run on the {where}. Remove it there first to add it here."));
            }

            if (existing is not null)
            {
                // Already added for the other purpose: offered for this one too, with the settings given for it.
                if (!existing.IsOffered(purpose))
                {
                    ImageModelDefinition widened = selfies
                        ? existing with { Purposes = existing.Purposes | purpose, SelfieSettings = settings }
                        : existing with { Purposes = existing.Purposes | purpose, Settings = settings };
                    List<ImageModelDefinition> updated = [.. _userModels.Select(candidate => candidate == existing ? widened : candidate)];
                    SaveUserModels(updated);
                    _userModels.Clear();
                    _userModels.AddRange(updated);
                }

                return Task.FromResult(new LocalModelAddResult(existing.Id, null));
            }

            string family = file.Family switch
            {
                ImageModelFamily.StableDiffusionXL => "An SDXL model",
                ImageModelFamily.Flux2Klein4B => "A FLUX.2 [klein] 4B model",
                _ => "A Stable Diffusion 1.x model"
            };
            string shared = file.Family == ImageModelFamily.Flux2Klein4B
                ? " It is drawn with the Qwen3-4B text encoder and FLUX.2 VAE (2.8 GB), downloaded with it unless another FLUX.2 [klein] 4B model already did."
                : string.Empty;
            string selfieNote = file.Family == ImageModelFamily.Flux2Klein4B
                ? " Selfies keep the face by drawing from the contact's photo itself."
                : " Downloading it also downloads face matching for it if needed.";

            ImageModelDefinition model = new(
                LocalModelLibrary.ModelId(lookup.RepoId, checkpoint.Name),
                Path.GetFileNameWithoutExtension(checkpoint.LocalName) + (file.Quantization is { } quantization ? $" · {quantization}" : string.Empty),
                $"From {lookup.RepoId} on Hugging Face. {family} whose compatibility with this app has not been verified.{shared}",
                lookup.License ?? "See the repository",
                lookup.Url,
                $"{HuggingFaceClient.SITE}{lookup.RepoId}/resolve/{lookup.Revision}/",
                [checkpoint],
                settings,
                false,
                lookup.RepoId,
                purpose,
                selfies ? settings : null,
                selfies ? $"From {lookup.RepoId} on Hugging Face, for selfies. {family} whose compatibility with this app has not been verified.{shared}{selfieNote}" : null,
                lookup.Processor,
                file.Family,
                promptStyle,
                file.Family == ImageModelFamily.Flux2Klein4B ? ImageModelCatalog.KleinComponents : null);

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
        KeepCompleteFiles(ModelDirectory(model), model.Files);
    }

    private static void KeepCompleteFiles(string directory, IReadOnlyList<LocalModelFile> files)
    {
        string partial = directory + LocalModelLibrary.PARTIAL_SUFFIX;

        if (!Directory.Exists(directory))
        {
            return;
        }

        Directory.CreateDirectory(partial);

        foreach (LocalModelFile file in files)
        {
            string source = Path.Combine(directory, file.LocalName);
            string target = Path.Combine(partial, file.LocalName);

            if (File.Exists(source) && !File.Exists(target) && new FileInfo(source).Length == file.Size)
            {
                // Files are kept by name alone, so this is the download folder itself; created anyway in case that changes.
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
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

    private string? LoadSelection(string name)
    {
        string path = Path.Combine(_directory, name);


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

    /// <summary>
    /// Reports one part of a download as progress through the whole, which starts <paramref name="offset"/> bytes in.
    /// </summary>
    private sealed class OffsetProgress : IProgress<LocalModelDownloadProgress>
    {
        private readonly IProgress<LocalModelDownloadProgress> _whole;
        private readonly long _offset;
        private readonly long _total;

        public OffsetProgress(IProgress<LocalModelDownloadProgress> whole, long offset, long total)
        {
            _whole = whole;
            _offset = offset;
            _total = total;
        }

        public void Report(LocalModelDownloadProgress value)
        {
            _whole.Report(new LocalModelDownloadProgress(_offset + value.BytesDownloaded, Math.Max(_total, _offset + value.TotalBytes)));
        }
    }
}
