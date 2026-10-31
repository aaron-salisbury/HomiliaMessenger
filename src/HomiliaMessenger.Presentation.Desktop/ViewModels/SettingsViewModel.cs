using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Presentation.Desktop.Base;
using HomiliaMessenger.Presentation.Desktop.Settings;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using System;

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

    public SettingsViewModel(ThemeService themeService)
    {
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));

        AppDisplayName = AppInfo.AppDisplayName;
        ApplicationInfo = $"{AppInfo.AppDisplayName} - {AppInfo.Version}";
        CopyHolder = AppInfo.CopyHolder;
        AppDescription = AppInfo.AppDescription;
        PrivacyURL = AppInfo.PrivacyURL;
        IssuesURL = AppInfo.IssuesURL;

        SetThemeSelection(_themeService.CurrentPreference);
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
