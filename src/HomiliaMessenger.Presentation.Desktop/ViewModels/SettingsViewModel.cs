using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Memories;
using HomiliaMessenger.Business.Modules.Speech;
using HomiliaMessenger.Presentation.Desktop.Base;
using HomiliaMessenger.Presentation.Desktop.Models;
using HomiliaMessenger.Presentation.Desktop.Settings;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using System;
using System.Collections.ObjectModel;
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

    /// <summary>
    /// Supplied by the view to ask the user to confirm an action, given a title and a message.
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// Supplied by the view to show the add-model dialog. Returns the added model's name, or <see langword="null"/>.
    /// </summary>
    public Func<AddLocalModelViewModel, Task<string?>>? ShowAddModelAsync { get; set; }

    public SettingsViewModel(ThemeService themeService, ILocalModelLibrary localModels, ITextToSpeech? speech = null, IEmbeddingModelLibrary? embeddingModels = null, IMemoryService? memories = null)
    {
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

    public override Task InitializeAsync()
    {
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

        return Task.CompletedTask;
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
