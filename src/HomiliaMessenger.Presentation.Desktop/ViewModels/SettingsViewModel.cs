using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Presentation.Desktop.Base;
using HomiliaMessenger.Presentation.Desktop.Models;
using HomiliaMessenger.Presentation.Desktop.Settings;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Linq;
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

    /// <summary>
    /// Models that run on this device. Kept for the view model's lifetime so a download continues while the user is on
    /// another page.
    /// </summary>
    public ObservableCollection<LocalModelItem> LocalModels { get; } = [];

    public string LocalModelsHint { get; }

    /// <summary>
    /// Supplied by the view to ask the user to confirm an action, given a title and a message.
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public SettingsViewModel(ThemeService themeService, ILocalModelLibrary localModels)
    {
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _localModels = localModels ?? throw new ArgumentNullException(nameof(localModels));
        LocalModelsHint = $"Run a model on this computer, without a GPU or an account. Download one, then set a contact's agent provider to \"{_localModels.Provider}\" and its model to the model's name. Contacts without an agent reply with simulated text.";

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
        if (LocalModels.Count == 0)
        {
            foreach (LocalModel model in _localModels.List())
            {
                LocalModels.Add(new LocalModelItem(model, _localModels, ConfirmWithViewAsync));
            }
        }
        else
        {
            // Files may have changed since the page was last shown.
            foreach (LocalModelItem item in LocalModels.Where(item => !item.IsDownloading))
            {
                item.Refresh();
            }
        }

        return Task.CompletedTask;
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
