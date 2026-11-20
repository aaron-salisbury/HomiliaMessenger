using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentValidation.Results;
using HomiliaMessenger.Business.Base.Extensions;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Business.Modules.Memories;
using HomiliaMessenger.Business.Modules.Speech;
using HomiliaMessenger.Business.Modules.WebSearch;
using HomiliaMessenger.Presentation.Desktop.Base;
using HomiliaMessenger.Presentation.Desktop.Models;
using HomiliaMessenger.Presentation.Desktop.Settings;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.ViewModels;

public partial class SettingsViewModel : BaseViewModel
{
    [ObservableProperty] private bool _themeIsSystem;
    [ObservableProperty] private bool _themeIsLight;
    [ObservableProperty] private bool _themeIsDark;

    public string AppDisplayName { get; }
    public string ApplicationInfo { get; }
    public string CopyHolder { get; }
    public string AppDescription { get; }
    public string PrivacyURL { get; }
    public string IssuesURL { get; }

    private readonly ThemeService _themeService;
    private readonly ILocalModelLibrary _localModels;
    private readonly IEmbeddingModelLibrary? _embeddingModels;
    private readonly IMemoryService? _memories;
    private readonly ITextToSpeech? _speech;
    private CancellationTokenSource? _speechDownload;
    private bool _refreshingSpeech;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadSpeech))]
    private bool _hasSpeechModel;
    [ObservableProperty] private bool _speechEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadSpeech))]
    private bool _isSpeechDownloading;
    [ObservableProperty] private double _speechProgress;
    [ObservableProperty] private string _speechStatus = "Not downloaded. Speech is optional.";

    public bool CanDownloadSpeech => !HasSpeechModel && !IsSpeechDownloading;

    private readonly IWebSearchService? _webSearch;
    private bool _refreshingWebSearch;

    // Saves run one at a time, and only the latest change is saved, so a slow earlier save can never overwrite (or
    // redisplay) a newer choice, such as turning web search off right after turning it on.
    private readonly SemaphoreSlim _webSearchSaveGate = new(1, 1);
    private int _webSearchChange;
    // A key action may refresh the page while a setting is still saving. Wait for that save to finish before showing
    // database values, including when the save was rejected and the controls need to return to their stored values.
    private int _webSearchResolvedChange;

    public bool IsWebSearchAvailable => _webSearch is not null;
    [ObservableProperty] private bool _webSearchEnabled;
    [ObservableProperty] private bool _tavilyEnabled = true;
    [ObservableProperty] private decimal? _monthlyCreditBudget = WebSearchSettings.DEFAULT_MONTHLY_CREDIT_BUDGET;
    [ObservableProperty] private decimal? _creditResetDay = 1;
    [ObservableProperty] private bool _hasTavilyKey;
    [ObservableProperty] private string _tavilyKeyInput = string.Empty;
    [ObservableProperty] private string _webSearchUsage = string.Empty;
    [ObservableProperty] private string _webSearchStatus = string.Empty;
    [ObservableProperty] private bool _isCheckingTavilyKey;

    public string WebSearchHint { get; } = "Let contacts look up current information, such as a new release or recent news, when a message calls for it. Only a short query made from your latest message is sent to Tavily, an online search service; never your conversation, memories, or profile. Off until you turn it on, and it needs a Tavily API key.";

    /// <summary>
    /// Models that run on this device. Kept for the view model's lifetime so a download continues while the user is on
    /// another page.
    /// </summary>
    public ObservableCollection<LocalModelItem> LocalModels { get; } = [];

    public string LocalModelsHint { get; }

    /// <summary>
    /// Models that memories can be searched with, kept apart from the models contacts converse with.
    /// </summary>
    public ObservableCollection<EmbeddingModelItem> EmbeddingModels { get; } = [];

    public string EmbeddingModelsHint { get; }

    private readonly IImageModelLibrary? _imageModels;
    private readonly IImageGenerator? _imageGenerator;
    private readonly IImageGpuRuntime? _gpuRuntime;
    private readonly IImageLoraLibrary? _loras;
    private CancellationTokenSource? _cudaDownload;
    private bool _refreshingGpu;

    public bool IsImageGenerationAvailable => _imageModels is not null;

    public string ImageGenerationHint { get; } = "Generate contact photos and let contacts send selfies, on this computer. One model is in use for photos and one for selfies at a time, chosen from either list below. Optional.";

    public string CpuImageHint { get; } = "These models run on the computer's processor, so they work on any computer, without a graphics card. They are slower, and follow simpler descriptions.";

    /// <summary>
    /// Processor models that draw contact photos, kept apart from the models contacts converse with. Kept for the view
    /// model's lifetime so a download continues while the user is on another page.
    /// </summary>
    public ObservableCollection<ImageModelItem> ImageModels { get; } = [];

    public string ImageModelsHint { get; } = "Generate contact photos from a description. Download an image model, then choose Generate photo beside a contact's photo on the Contacts page. Uses about 4 GB of memory while a photo is made.";

    /// <summary>
    /// Processor models contacts take selfies with, which also need face matching. A model may be listed under both.
    /// </summary>
    public ObservableCollection<ImageModelItem> SelfieModels { get; } = [];

    public string SelfieModelsHint { get; } = "Let contacts send selfies in chats: the camera button in a chat asks for one, and the contact decides what it shows from the conversation. A selfie keeps the face in the contact's profile photo, so it needs a contact with a photo, and face matching besides the model. Only one person is in each selfie; in a group, the most likely contact is chosen. Uses about 4.5 GB of memory while a selfie is made.";

    public string GpuImageHint { get; } = "These models run on a graphics card: several times faster than the processor, more realistic, and they follow long, specific descriptions (being splashed, a dog licking a cheek, a sign with words on it). They need about 8 GB of graphics memory, or run more slowly on smaller cards by keeping part of the model in the computer's memory.";

    /// <summary>
    /// Graphics card models that draw contact photos.
    /// </summary>
    public ObservableCollection<ImageModelItem> GpuImageModels { get; } = [];

    public string GpuImageModelsHint { get; } = "FLUX.2 [klein] 4B is recommended: about 5 seconds a photo on a recent NVIDIA card with the CUDA runtime. DreamShaper XL Lightning, an SDXL model, is offered for the many SDXL LoRAs that fit it.";

    /// <summary>
    /// Graphics card models contacts take selfies with.
    /// </summary>
    public ObservableCollection<ImageModelItem> GpuSelfieModels { get; } = [];

    public string GpuSelfieModelsHint { get; } = "FLUX.2 [klein] keeps the contact's face by drawing from their profile photo itself, needing nothing else: about 8 seconds a selfie with the CUDA runtime. DreamShaper XL Lightning keeps it with face matching for SDXL.";

    /// <summary>
    /// The graphics cards found, and which one graphics card models run on.
    /// </summary>
    [ObservableProperty] private string _gpuStatus = "Looking for graphics cards…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadCuda), nameof(CanRemoveCuda))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCudaCommand), nameof(RemoveCudaCommand))]
    private bool _isCudaOffered;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadCuda), nameof(CanRemoveCuda))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCudaCommand), nameof(RemoveCudaCommand))]
    private bool _isCudaDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadCuda), nameof(CanRemoveCuda))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCudaCommand), nameof(RemoveCudaCommand), nameof(CancelCudaDownloadCommand))]
    private bool _isCudaDownloading;

    [ObservableProperty] private double _cudaProgress;
    [ObservableProperty] private string _cudaStatus = string.Empty;
    [ObservableProperty] private string _cudaHint = string.Empty;

    public bool CanDownloadCuda => IsCudaOffered && !IsCudaDownloaded && !IsCudaDownloading;

    public bool CanRemoveCuda => IsCudaDownloaded && !IsCudaDownloading;

    public bool IsLoraAvailable => _loras is not null;

    public string LoraHint { get; } = "A LoRA is a small add-on that changes what a model draws: a style, a character, a kind of clothing. Choose the folder you keep LoRAs in (.safetensors files, such as from Civitai or Hugging Face); they stay there. Each is used only with models of the base it was made for, read from the file: SDXL 1.0 LoRAs with DreamShaper XL Lightning, SD 1.5 LoRAs with DreamShaper 8, and FLUX.2 [klein] 4B LoRAs with FLUX.2 [klein]. Pony and Illustrious LoRAs are SDXL too, but often suit DreamShaper XL poorly. Trigger words, which many LoRAs need, are filled in when the file or the notes beside it name them, and added to the start of every prompt the LoRA is used with.";

    /// <summary>
    /// The LoRAs in the chosen folder.
    /// </summary>
    public ObservableCollection<ImageLoraItem> Loras { get; } = [];

    private int _loraScan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoraFolder))]
    private string? _loraFolder;

    [ObservableProperty] private string _loraStatus = string.Empty;

    public bool HasLoraFolder => LoraFolder is not null;

    /// <summary>
    /// Supplied by the view to let the user choose a folder. Returns its path, or <see langword="null"/>.
    /// </summary>
    public Func<string, Task<string?>>? PickFolderAsync { get; set; }

    /// <summary>
    /// Whether face matching, which every selfie model needs, is downloaded, and what it takes.
    /// </summary>
    [ObservableProperty] private string _faceMatchingStatus = string.Empty;

    /// <summary>
    /// Why photos cannot be generated in this build or on this system, when that is so; downloading a model would not help.
    /// </summary>
    [ObservableProperty] private string? _imageRuntimeProblem;

    /// <summary>
    /// Supplied by the view to show the add-image-model dialog. Returns the added model's name, or <see langword="null"/>.
    /// </summary>
    public Func<AddImageModelViewModel, Task<string?>>? ShowAddImageModelAsync { get; set; }

    /// <summary>
    /// Supplied by the view to ask the user to confirm an action, given a title and a message.
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// Supplied by the view to show the add-model dialog. Returns the added model's name, or <see langword="null"/>.
    /// </summary>
    public Func<AddLocalModelViewModel, Task<string?>>? ShowAddModelAsync { get; set; }

    public SettingsViewModel(ThemeService themeService, ILocalModelLibrary localModels, ITextToSpeech? speech = null, IEmbeddingModelLibrary? embeddingModels = null, IMemoryService? memories = null, IWebSearchService? webSearch = null, IImageModelLibrary? imageModels = null, IImageGenerator? imageGenerator = null, IImageGpuRuntime? gpuRuntime = null, IImageLoraLibrary? loras = null)
    {
        _imageModels = imageModels;
        _imageGenerator = imageGenerator;
        _gpuRuntime = gpuRuntime;
        _loras = loras;
        _loraFolder = loras?.Folder;
        _webSearch = webSearch;
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _localModels = localModels ?? throw new ArgumentNullException(nameof(localModels));
        _speech = speech;
        _embeddingModels = embeddingModels;
        _memories = memories;
        EmbeddingModelsHint = "Contacts remember lasting things you tell them. With an embedding model, which runs on this computer, they recall the memories that relate to what you're talking about; without one, they recall memories that share its words. Optional.";
        LocalModelsHint = "Run a model on this computer, without a GPU or an account. Download one, or add any GGUF model from Hugging Face, then choose it as a contact's agent on the Contacts page. Contacts without an agent reply with simulated text.";

        AppDisplayName = AppInfo.AppDisplayName;
        ApplicationInfo = $"{AppInfo.AppDisplayName} - {AppInfo.Version}";
        CopyHolder = AppInfo.CopyHolder;
        AppDescription = AppInfo.AppDescription;
        PrivacyURL = AppInfo.PrivacyURL;
        IssuesURL = AppInfo.IssuesURL;

        SetThemeSelection(_themeService.CurrentPreference);
    }

    public override async Task InitializeAsync()
    {
        await RefreshWebSearchAsync();

        if (!IsSpeechDownloading)
        {
            RefreshSpeech();
        }

        if (LocalModels.Count == 0)
        {
            foreach (LocalModel model in _localModels.List())
            {
                LocalModels.Add(CreateItem(model));
            }
        }
        else
        {
            // Files may have changed since the page was last shown.
            foreach (LocalModelItem item in LocalModels.Where(item => !item.IsDownloading).ToList())
            {
                item.Refresh();
            }
        }

        RefreshImageModels();
        _ = RefreshGpuAsync();
        _ = ScanLorasAsync();

        if (_embeddingModels is not null)
        {
            if (EmbeddingModels.Count == 0)
            {
                foreach (EmbeddingModel model in _embeddingModels.List())
                {
                    EmbeddingModels.Add(new EmbeddingModelItem(model, _embeddingModels, ConfirmWithViewAsync, OnEmbeddingModelsChanged));
                }
            }
            else
            {
                foreach (EmbeddingModelItem item in EmbeddingModels.Where(item => !item.IsDownloading))
                {
                    item.Refresh();
                }
            }
        }
    }

    /// <summary>
    /// After an embedding model is downloaded, chosen, or removed: shows every model's state, which may all have changed,
    /// and embeds any memories the model in use has no vectors for, in the background.
    /// </summary>
    private void OnEmbeddingModelsChanged()
    {
        foreach (EmbeddingModelItem item in EmbeddingModels.Where(item => !item.IsDownloading))
        {
            item.Refresh();
        }

        if (_memories is not null)
        {
            _ = EmbedMemoriesAsync(_memories);
        }
    }

    /// <summary>
    /// Lists the image models for contact photos and for selfies, or shows their current state when they are already
    /// listed. Both lists change together: a model downloaded for one purpose may serve the other.
    /// </summary>
    private void RefreshImageModels()
    {
        if (_imageModels is null)
        {
            return;
        }

        ImageGeneratorAvailability? availability = _imageGenerator?.GetAvailability();
        ImageRuntimeProblem = availability is { IsRuntimeIncluded: false } ? availability.Problem : null;
        RefreshImageList(ImageModels, ImageModelPurpose.Portraits, ImageProcessor.Cpu);
        RefreshImageList(SelfieModels, ImageModelPurpose.Selfies, ImageProcessor.Cpu);
        RefreshImageList(GpuImageModels, ImageModelPurpose.Portraits, ImageProcessor.Gpu);
        RefreshImageList(GpuSelfieModels, ImageModelPurpose.Selfies, ImageProcessor.Gpu);

        FaceMatching faceMatching = _imageModels.GetFaceMatching();
        FaceMatchingStatus = faceMatching.IsDownloaded
            ? $"Face matching ({faceMatching.Name}, {faceMatching.License}) is downloaded: {FormatSize(faceMatching.DiskBytes)}."
            : $"Face matching ({faceMatching.Name}, {faceMatching.License}) is not downloaded. Downloading a selfie model downloads it too: {FormatSize(faceMatching.DownloadBytes)}, made {FormatSize(faceMatching.DiskBytes)} once prepared.";
    }

    private void RefreshImageList(ObservableCollection<ImageModelItem> items, ImageModelPurpose purpose, ImageProcessor processor)
    {
        IEnumerable<ImageModel> offered = _imageModels!.List(purpose).Where(model => model.Processor == processor);

        if (items.Count == 0)
        {
            foreach (ImageModel model in offered)
            {
                items.Add(CreateImageItem(model, items, purpose));
            }

            return;
        }

        foreach (ImageModelItem item in items.Where(item => !item.IsDownloading).ToList())
        {
            item.Refresh();
        }

        // A model added for the other purpose may now be offered for this one too.
        foreach (ImageModel model in offered.Where(model => items.All(item => item.Id != model.Id)))
        {
            items.Add(CreateImageItem(model, items, purpose));
        }
    }

    private ImageModelItem CreateImageItem(ImageModel model, ObservableCollection<ImageModelItem> items, ImageModelPurpose purpose)
    {
        ImageModelItem item = new(model, _imageModels!, ConfirmWithViewAsync, RefreshImageModels, purpose);
        item.Removed += (_, _) => items.Remove(item);
        return item;
    }

    /// <summary>
    /// Adds a Stable Diffusion 1.x model from Hugging Face for contact photos on the processor, then starts downloading it.
    /// </summary>
    [RelayCommand]
    private Task AddImageModelAsync()
    {
        return AddImageModelForAsync(ImageModels, ImageModelPurpose.Portraits, ImageProcessor.Cpu);
    }

    /// <summary>
    /// Adds a Stable Diffusion 1.x model from Hugging Face for selfies on the processor, then starts downloading it with
    /// face matching.
    /// </summary>
    [RelayCommand]
    private Task AddSelfieModelAsync()
    {
        return AddImageModelForAsync(SelfieModels, ImageModelPurpose.Selfies, ImageProcessor.Cpu);
    }

    /// <summary>
    /// Adds a model from Hugging Face for contact photos on a graphics card, then starts downloading it.
    /// </summary>
    [RelayCommand]
    private Task AddGpuImageModelAsync()
    {
        return AddImageModelForAsync(GpuImageModels, ImageModelPurpose.Portraits, ImageProcessor.Gpu);
    }

    /// <summary>
    /// Adds a model from Hugging Face for selfies on a graphics card, then starts downloading it.
    /// </summary>
    [RelayCommand]
    private Task AddGpuSelfieModelAsync()
    {
        return AddImageModelForAsync(GpuSelfieModels, ImageModelPurpose.Selfies, ImageProcessor.Gpu);
    }

    private async Task AddImageModelForAsync(ObservableCollection<ImageModelItem> items, ImageModelPurpose purpose, ImageProcessor processor)
    {
        if (_imageModels is null || ShowAddImageModelAsync is null || await ShowAddImageModelAsync(new AddImageModelViewModel(_imageModels, purpose, processor)) is not { } modelId)
        {
            return;
        }

        ImageModelItem? item = items.FirstOrDefault(existing => existing.Id == modelId);

        if (item is null && _imageModels.List(purpose).FirstOrDefault(model => model.Id == modelId) is { } added)
        {
            item = CreateImageItem(added, items, purpose);
            items.Add(item);
        }

        if (item is not null && item.DownloadCommand.CanExecute(null))
        {
            await item.DownloadCommand.ExecuteAsync(null);
        }
    }

    private static string FormatSize(long bytes)
    {
        return bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000d:0.0} GB" : $"{bytes / 1_000_000d:0} MB";
    }

    /// <summary>
    /// Finds the graphics cards, off the UI thread (the first time loads the image generation runtime), and shows them with
    /// the CUDA runtime's state.
    /// </summary>
    private async Task RefreshGpuAsync()
    {
        if (_gpuRuntime is null || _refreshingGpu || IsCudaDownloading)
        {
            return;
        }

        _refreshingGpu = true;

        try
        {
            ImageGpuStatus status = await Task.Run(_gpuRuntime.GetStatus);
            ShowGpu(status);
        }
        catch (Exception error)
        {
            GpuStatus = "Could not look for graphics cards: " + error.Message;
        }
        finally
        {
            _refreshingGpu = false;
        }
    }

    private void ShowGpu(ImageGpuStatus status)
    {
        IsCudaOffered = status.IsCudaOffered;
        IsCudaDownloaded = status.IsCudaDownloaded;

        if (status.Preferred is not { } device)
        {
            GpuStatus = (status.Problem ?? "No graphics card was found.") + " Graphics card models can be downloaded, but not used here; choose a processor model instead.";
        }
        else
        {
            string memory = device.TotalBytes > 0 ? $", {FormatSize(device.TotalBytes)}" : string.Empty;
            string backend = device.IsCuda ? "CUDA" : "Vulkan";
            GpuStatus = $"Graphics card models run on {device.Description} ({backend}{memory}).";

            if (device.IsIntegrated)
            {
                GpuStatus += " It shares the computer's memory, so graphics card models may be slow on it.";
            }
        }

        CudaHint = !status.IsCudaOffered
            ? string.Empty
            : status.IsCudaActive
                ? "The CUDA runtime is in use."
                : status.IsCudaDownloaded
                    ? "The CUDA runtime is downloaded. Restart Homilia Messenger to use it."
                    : $"For an NVIDIA card, the CUDA runtime makes graphics card models several times faster than the Vulkan backend included with the app, above all for FLUX.2 [klein] (on an RTX 4070 Ti, a photo in about 4 seconds rather than 24, a selfie in 7 rather than 13). {FormatSize(status.CudaDownloadBytes)} to download, {FormatSize(status.CudaDiskBytes)} unpacked; NVIDIA's CUDA libraries in it are redistributed under NVIDIA's license.";
    }

    [RelayCommand(CanExecute = nameof(CanDownloadCuda))]
    private async Task DownloadCudaAsync()
    {
        if (_gpuRuntime is null)
        {
            return;
        }

        using CancellationTokenSource download = new();
        _cudaDownload = download;
        IsCudaDownloading = true;
        CudaStatus = "Starting download…";

        Progress<LocalModelDownloadProgress> progress = new(update =>
        {
            CudaProgress = update.TotalBytes == 0 ? 0 : 100d * update.BytesDownloaded / update.TotalBytes;
            CudaStatus = update.BytesDownloaded >= update.TotalBytes
                ? "Verifying and unpacking…"
                : $"{FormatSize(update.BytesDownloaded)} of {FormatSize(update.TotalBytes)}";
        });

        try
        {
            LocalModelDownloadResult result = await _gpuRuntime.DownloadCudaAsync(progress, download.Token);

            CudaStatus = result.Status switch
            {
                LocalModelDownloadStatus.Success => "Ready.",
                LocalModelDownloadStatus.Canceled => "Download paused. Download again to resume.",
                _ => result.Problem ?? "The download failed."
            };
        }
        catch (Exception error)
        {
            CudaStatus = "The download failed: " + error.Message;
        }
        finally
        {
            _cudaDownload = null;
            IsCudaDownloading = false;
        }

        await RefreshGpuAsync();
    }

    private bool CanCancelCudaDownload()
    {
        return IsCudaDownloading;
    }

    [RelayCommand(CanExecute = nameof(CanCancelCudaDownload))]
    private void CancelCudaDownload()
    {
        _cudaDownload?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveCuda))]
    private async Task RemoveCudaAsync()
    {
        if (_gpuRuntime is null || !await ConfirmWithViewAsync("Remove the CUDA runtime", "Remove the CUDA runtime? Graphics card models will run with the Vulkan backend included with the app, which is several times slower on NVIDIA cards."))
        {
            return;
        }

        try
        {
            CudaStatus = await _gpuRuntime.DeleteCudaAsync() ? "Removed." : "The CUDA runtime is in use. It is removed when Homilia Messenger restarts.";
        }
        catch (Exception error)
        {
            CudaStatus = "Could not remove the CUDA runtime: " + error.Message;
        }

        await RefreshGpuAsync();
    }

    /// <summary>
    /// Lets the user choose the folder they keep LoRAs in, then lists them.
    /// </summary>
    [RelayCommand]
    private async Task ChooseLoraFolderAsync()
    {
        if (_loras is null || PickFolderAsync is null || await PickFolderAsync("Choose the folder you keep LoRAs in") is not { } folder)
        {
            return;
        }

        if (_loras.SetFolder(folder) is { } problem)
        {
            LoraStatus = problem;
            return;
        }

        LoraFolder = _loras.Folder;
        await ScanLorasAsync();
    }

    [RelayCommand]
    private async Task ClearLoraFolderAsync()
    {
        if (_loras is null)
        {
            return;
        }

        if (_loras.SetFolder(null) is { } problem)
        {
            LoraStatus = problem;
            return;
        }

        LoraFolder = null;
        await ScanLorasAsync();
    }

    /// <summary>
    /// Lists the LoRAs in the folder, reading each one's header for its base model and trigger words.
    /// </summary>
    [RelayCommand]
    private async Task ScanLorasAsync()
    {
        if (_loras is null || _imageModels is null)
        {
            return;
        }

        // Only the latest scan is shown, and only while its folder is still the one chosen; the list is emptied
        // meanwhile, so no LoRA from another folder is changed while it is read.
        int scan = ++_loraScan;
        string? folder = _loras.Folder;
        Loras.Clear();

        if (folder is null)
        {
            LoraStatus = "No folder chosen.";
            return;
        }

        LoraStatus = "Reading LoRAs…";

        try
        {
            IReadOnlyList<ImageLora> found = await _loras.ScanAsync();

            if (scan != _loraScan || !string.Equals(folder, _loras.Folder, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            IReadOnlyList<ImageModel> models = [.. _imageModels.List(ImageModelPurpose.Portraits), .. _imageModels.List(ImageModelPurpose.Selfies)];
            Loras.Clear();

            foreach (ImageLora lora in found)
            {
                ImageLoraItem item = new(lora, _loras, models);
                item.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName is nameof(ImageLoraItem.IsEnabled) or nameof(ImageLoraItem.ForPhotos) or nameof(ImageLoraItem.ForSelfies))
                    {
                        UpdateLoraStatus();
                    }
                };
                Loras.Add(item);
            }

            UpdateLoraStatus();
        }
        catch (Exception error) when (scan == _loraScan)
        {
            LoraStatus = "Could not read the LoRA folder: " + error.Message;
        }
        catch (Exception)
        {
            // A later scan reports for itself.
        }
    }

    /// <summary>
    /// Says how many LoRAs were found, how many fit, and how many are on, since a LoRA is off until it is turned on.
    /// </summary>
    private void UpdateLoraStatus()
    {
        if (Loras.Count == 0)
        {
            LoraStatus = "No LoRAs (.safetensors files) were found in this folder.";
            return;
        }

        int usable = Loras.Count(item => item.IsUsable);
        int on = Loras.Count(item => item.IsUsable && item.IsEnabled && (item.ForPhotos || item.ForSelfies));
        string found = $"{Loras.Count} {(Loras.Count == 1 ? "LoRA" : "LoRAs")} found; {usable} {(usable == 1 ? "fits" : "fit")} a model offered here. ";

        LoraStatus = found + (on == 0
            ? "None is on yet: turn one on to use it."
            : $"{on} {(on == 1 ? "is" : "are")} on, used with the photo and selfie models {(on == 1 ? "it fits" : "they fit")}.");
    }


    private static async Task EmbedMemoriesAsync(IMemoryService memories)
    {
        try
        {
            await memories.EmbedPendingAsync(null);
        }
        catch (Exception)
        {
            // Memories waiting for a vector are embedded after each reply as well; until then they are found by words.
        }
    }

    private LocalModelItem CreateItem(LocalModel model)
    {
        LocalModelItem item = new(model, _localModels, ConfirmWithViewAsync);
        item.Removed += (_, _) => LocalModels.Remove(item);
        return item;
    }

    /// <summary>
    /// Adds a GGUF model from Hugging Face, then starts downloading it.
    /// </summary>
    [RelayCommand]
    private async Task AddModelAsync()
    {
        if (ShowAddModelAsync is null || await ShowAddModelAsync(new AddLocalModelViewModel(_localModels)) is not { } modelId)
        {
            return;
        }

        LocalModelItem? item = LocalModels.FirstOrDefault(existing => existing.Id == modelId);

        if (item is null && _localModels.List().FirstOrDefault(model => model.Id == modelId) is { } added)
        {
            item = CreateItem(added);
            LocalModels.Add(item);
        }

        if (item is not null && item.DownloadCommand.CanExecute(null))
        {
            await item.DownloadCommand.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// Shows the speech model's actual state, which also puts the "Use Kokoro" box back if a change could not be made.
    /// </summary>
    private void RefreshSpeech()
    {
        HasSpeechModel = _speech?.Model.IsDownloaded == true;
        _refreshingSpeech = true;

        try
        {
            SpeechEnabled = _speech?.Model.IsAvailable == true;
        }
        finally
        {
            _refreshingSpeech = false;
        }

        SpeechStatus = SpeechEnabled ? "Kokoro is ready. Choose each contact's voice on the Contacts page."
            : HasSpeechModel ? "Kokoro is downloaded. Enable it to choose voices and hear replies."
            : "Not downloaded. Speech is optional.";
    }

    [RelayCommand]
    private async Task DownloadSpeechAsync()
    {
        if (_speech is null || !CanDownloadSpeech)
        {
            return;
        }

        using CancellationTokenSource download = new();
        _speechDownload = download;
        IsSpeechDownloading = true;
        SpeechProgress = 0;
        SpeechStatus = "Downloading Kokoro…";
        try
        {
            SpeechResult result = await _speech.DownloadAsync(new Progress<double>(value => SpeechProgress = value), download.Token);
            if (result.IsSuccessful)
            {
                await _speech.SetEnabledAsync(true, download.Token);
                RefreshSpeech();
            }
            else
            {
                SpeechStatus = result.Problem ?? "Could not download Kokoro.";
            }
        }
        catch (OperationCanceledException) when (download.IsCancellationRequested)
        {
            RefreshSpeech();
            SpeechStatus = "Download canceled. You can start it again.";
        }
        catch (Exception error)
        {
            RefreshSpeech();
            SpeechStatus = "Could not set up speech: " + error.Message;
        }
        finally
        {
            _speechDownload = null;
            IsSpeechDownloading = false;
        }
    }

    [RelayCommand]
    private void CancelSpeechDownload()
    {
        _speechDownload?.Cancel();
    }

    partial void OnSpeechEnabledChanged(bool value)
    {
        if (!_refreshingSpeech)
        {
            _ = ApplySpeechEnabledAsync(value);
        }
    }

    /// <summary>
    /// Turns Kokoro on or off when the user ticks "Use Kokoro". If that cannot be done, the box returns to the model's
    /// actual state rather than showing a change that did not happen.
    /// </summary>
    private async Task ApplySpeechEnabledAsync(bool enabled)
    {
        // Let the check box finish its own update before it may be put back.
        await Task.Yield();

        if (_speech is null || !HasSpeechModel || IsSpeechDownloading)
        {
            RefreshSpeech();
            return;
        }

        try
        {
            await _speech.SetEnabledAsync(enabled);
            RefreshSpeech();
        }
        catch (Exception error)
        {
            RefreshSpeech();
            SpeechStatus = "Could not change speech settings: " + error.Message;
        }
    }

    [RelayCommand]
    private async Task RemoveSpeechModelAsync()
    {
        if (_speech is null || IsSpeechDownloading
            || !await ConfirmWithViewAsync("Remove Kokoro", "Remove the downloaded speech model? Saved contact voices will be kept."))
        {
            return;
        }

        try
        {
            await _speech.RemoveAsync();
            RefreshSpeech();
        }
        catch (Exception error)
        {
            RefreshSpeech();
            SpeechStatus = "Could not remove Kokoro: " + error.Message;
        }
    }

    /// <summary>
    /// Shows web search's settings, key, and credit count as they are now.
    /// </summary>
    private async Task RefreshWebSearchAsync()
    {
        if (_webSearch is null)
        {
            return;
        }

        try
        {
            int change = _webSearchChange;
            WebSearchOverview overview = await _webSearch.GetOverviewAsync();

            // The user changed a setting while this was read; showing it would put back the old value, which the
            // pending save would then store. That save shows the result when it is done.
            if (change == _webSearchChange && change == _webSearchResolvedChange)
            {
                ShowWebSearch(overview);
            }
        }
        catch (Exception error)
        {
            WebSearchStatus = "Could not read web search settings: " + error.Message;
        }
    }

    private void ShowWebSearch(WebSearchOverview overview)
    {
        _refreshingWebSearch = true;

        try
        {
            WebSearchEnabled = overview.Settings.Enabled;
            TavilyEnabled = overview.Settings.TavilyEnabled;
            MonthlyCreditBudget = overview.Settings.MonthlyCreditBudget;
            CreditResetDay = overview.Settings.CreditResetDay;
            HasTavilyKey = overview.HasTavilyKey;
        }
        finally
        {
            _refreshingWebSearch = false;
        }

        string since = overview.PeriodStart.ToString("MMMM d", CultureInfo.CurrentCulture);
        WebSearchUsage = $"About {overview.CreditsUsed} of {overview.Settings.MonthlyCreditBudget} credits used since {since}, {overview.CreditsRemaining} left before searching pauses. This is counted on this computer; Tavily's dashboard has the real balance.";

        WebSearchStatus = overview.State switch
        {
            { KeyRejected: true } => overview.State.Problem ?? "Tavily rejected the saved key.",
            { PausedUntilUtc: { } until } when until > DateTimeOffset.UtcNow => $"{overview.State.Problem ?? "Searching is paused."} Paused until {until.ToLocalTime():g}.",
            { Problem: { } problem } => "Last problem: " + problem,
            _ when !overview.Settings.Enabled => "Web search is off.",
            _ when !overview.HasTavilyKey => "Save a Tavily API key to let contacts search.",
            _ when !overview.Settings.TavilyEnabled => "Tavily is turned off, so contacts will not search.",
            _ when overview.CreditsRemaining == 0 => "This period's credit budget is used up; searching resumes next period.",
            _ => "Ready. Contacts search only when a message calls for it."
        };
    }

    partial void OnWebSearchEnabledChanged(bool value)
    {
        SaveWebSearchSettingsSoon();
    }

    partial void OnTavilyEnabledChanged(bool value)
    {
        SaveWebSearchSettingsSoon();
    }

    partial void OnMonthlyCreditBudgetChanged(decimal? value)
    {
        SaveWebSearchSettingsSoon();
    }

    partial void OnCreditResetDayChanged(decimal? value)
    {
        SaveWebSearchSettingsSoon();
    }

    private void SaveWebSearchSettingsSoon()
    {
        if (!_refreshingWebSearch && _webSearch is not null)
        {
            WebSearchSettings settings = new(
                WebSearchEnabled,
                TavilyEnabled,
                (int)Math.Round(MonthlyCreditBudget ?? -1),
                (int)Math.Round(CreditResetDay ?? 0));
            _ = SaveWebSearchSettingsAsync(_webSearch, ++_webSearchChange, settings);
        }
    }

    /// <summary>
    /// Saves web search's settings as they are changed. Invalid values are not saved, and the page goes back to what is.
    /// A save superseded by a newer change before its turn is skipped, and does not refresh the page.
    /// </summary>
    internal async Task SaveWebSearchSettingsAsync(IWebSearchService webSearch, int change, WebSearchSettings settings)
    {
        // Let the control finish its own update before it may be put back.
        await Task.Yield();
        await _webSearchSaveGate.WaitAsync();

        try
        {
            if (change != _webSearchChange)
            {
                return;
            }

            try
            {
                ValidationResult result = await webSearch.SaveSettingsAsync(settings);

                if (change == _webSearchChange)
                {
                    _webSearchResolvedChange = change;
                    await RefreshWebSearchAsync();

                    if (!result.IsValid)
                    {
                        WebSearchStatus = result.ToStringPretty("Web search");
                    }
                }
            }
            catch (Exception error)
            {
                if (change == _webSearchChange)
                {
                    _webSearchResolvedChange = change;
                    await RefreshWebSearchAsync();
                    WebSearchStatus = "Could not save web search settings: " + error.Message;
                }
            }
        }
        finally
        {
            _webSearchSaveGate.Release();
        }
    }

    [RelayCommand]
    private async Task SaveTavilyKeyAsync()
    {
        if (_webSearch is null || string.IsNullOrWhiteSpace(TavilyKeyInput))
        {
            return;
        }

        try
        {
            await _webSearch.SetTavilyKeyAsync(TavilyKeyInput);
            TavilyKeyInput = string.Empty;
            await RefreshWebSearchAsync();
            WebSearchStatus = "Key saved in the system's credential store. Check it to confirm it works.";
        }
        catch (Exception error)
        {
            WebSearchStatus = "Could not save the key: " + error.Message;
        }
    }

    [RelayCommand]
    private async Task RemoveTavilyKeyAsync()
    {
        if (_webSearch is null || !HasTavilyKey
            || !await ConfirmWithViewAsync("Remove Tavily key", "Remove the saved Tavily API key? Contacts will stop searching until you save another."))
        {
            return;
        }

        try
        {
            await _webSearch.SetTavilyKeyAsync(null);
            await RefreshWebSearchAsync();
        }
        catch (Exception error)
        {
            WebSearchStatus = "Could not remove the key: " + error.Message;
        }
    }

    /// <summary>
    /// Asks Tavily whether the saved key works and how many credits it has used, which costs no credits.
    /// </summary>
    [RelayCommand]
    private async Task CheckTavilyKeyAsync()
    {
        if (_webSearch is null || IsCheckingTavilyKey)
        {
            return;
        }

        IsCheckingTavilyKey = true;

        try
        {
            WebSearchKeyCheck check = await _webSearch.CheckTavilyKeyAsync();
            await RefreshWebSearchAsync();

            if (check.Status == Business.Modules.WebSearch.WebSearchStatus.Success)
            {
                string key = check.KeyCreditsUsed is { } used ? $" Tavily says this key has used {used} credits this cycle." : string.Empty;
                string plan = check.PlanCreditsUsed is { } planUsed && check.PlanCreditLimit is { } limit ? $" The account has used {planUsed} of {limit}." : string.Empty;
                WebSearchStatus = "The key works." + key + plan;
            }
            else
            {
                WebSearchStatus = check.Problem ?? "Tavily could not confirm the key.";
            }
        }
        catch (Exception error)
        {
            WebSearchStatus = "Could not check the key: " + error.Message;
        }
        finally
        {
            IsCheckingTavilyKey = false;
        }
    }

    private Task<bool> ConfirmWithViewAsync(string title, string message)
    {
        return ConfirmAsync is null ? Task.FromResult(false) : ConfirmAsync(title, message);
    }

    [RelayCommand]
    private void SetTheme(string choice)
    {
        ThemePreference preference = choice.ToLowerInvariant() switch
        {
            "light" => ThemePreference.Light,
            "dark" => ThemePreference.Dark,
            _ => ThemePreference.System
        };

        try
        {
            _themeService.Set(preference);
            SetThemeSelection(preference);
        }
        catch (Exception)
        {
            SetThemeSelection(_themeService.CurrentPreference);
        }
    }

    private void SetThemeSelection(ThemePreference preference)
    {
        ThemeIsSystem = preference == ThemePreference.System;
        ThemeIsLight = preference == ThemePreference.Light;
        ThemeIsDark = preference == ThemePreference.Dark;
    }
}
