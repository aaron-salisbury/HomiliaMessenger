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

    public bool IsImageGenerationAvailable => _imageModels is not null;

    /// <summary>
    /// Models that draw contact photos, kept apart from the models contacts converse with. Kept for the view model's
    /// lifetime so a download continues while the user is on another page.
    /// </summary>
    public ObservableCollection<ImageModelItem> ImageModels { get; } = [];

    public string ImageModelsHint { get; } = "Generate contact photos from a description, on this computer. Download an image model, then choose Generate photo beside a contact's photo on the Contacts page. Optional; works without a GPU, using about 4 GB of memory while a photo is made.";

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

    public SettingsViewModel(ThemeService themeService, ILocalModelLibrary localModels, ITextToSpeech? speech = null, IEmbeddingModelLibrary? embeddingModels = null, IMemoryService? memories = null, IWebSearchService? webSearch = null, IImageModelLibrary? imageModels = null, IImageGenerator? imageGenerator = null)
    {
        _imageModels = imageModels;
        _imageGenerator = imageGenerator;
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
    /// Lists the image models, or shows their current state when they are already listed.
    /// </summary>
    private void RefreshImageModels()
    {
        if (_imageModels is null)
        {
            return;
        }

        ImageGeneratorAvailability? availability = _imageGenerator?.GetAvailability();
        ImageRuntimeProblem = availability is { IsRuntimeIncluded: false } ? availability.Problem : null;

        if (ImageModels.Count == 0)
        {
            foreach (ImageModel model in _imageModels.List())
            {
                ImageModels.Add(CreateImageItem(model));
            }

            return;
        }

        foreach (ImageModelItem item in ImageModels.Where(item => !item.IsDownloading).ToList())
        {
            item.Refresh();
        }
    }

    private ImageModelItem CreateImageItem(ImageModel model)
    {
        ImageModelItem item = new(model, _imageModels!, ConfirmWithViewAsync, RefreshImageModels);
        item.Removed += (_, _) => ImageModels.Remove(item);
        return item;
    }

    /// <summary>
    /// Adds a Stable Diffusion 1.x model from Hugging Face, then starts downloading it.
    /// </summary>
    [RelayCommand]
    private async Task AddImageModelAsync()
    {
        if (_imageModels is null || ShowAddImageModelAsync is null || await ShowAddImageModelAsync(new AddImageModelViewModel(_imageModels)) is not { } modelId)
        {
            return;
        }

        ImageModelItem? item = ImageModels.FirstOrDefault(existing => existing.Id == modelId);

        if (item is null && _imageModels.List().FirstOrDefault(model => model.Id == modelId) is { } added)
        {
            item = CreateImageItem(added);
            ImageModels.Add(item);
        }

        if (item is not null && item.DownloadCommand.CanExecute(null))
        {
            await item.DownloadCommand.ExecuteAsync(null);
        }
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
            if (change == _webSearchChange)
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
            _ = SaveWebSearchSettingsAsync(_webSearch, ++_webSearchChange);
        }
    }

    /// <summary>
    /// Saves web search's settings as they are changed. Invalid values are not saved, and the page goes back to what is.
    /// A save superseded by a newer change before its turn is skipped, and does not refresh the page.
    /// </summary>
    internal async Task SaveWebSearchSettingsAsync(IWebSearchService webSearch, int change)
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

            WebSearchSettings settings = new(
                WebSearchEnabled,
                TavilyEnabled,
                (int)Math.Round(MonthlyCreditBudget ?? -1),
                (int)Math.Round(CreditResetDay ?? 0));

            try
            {
                ValidationResult result = await webSearch.SaveSettingsAsync(settings);

                if (change == _webSearchChange)
                {
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
