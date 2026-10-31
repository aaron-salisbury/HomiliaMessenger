using Avalonia;
using Avalonia.Styling;
using System;

namespace HomiliaMessenger.Presentation.Desktop.Settings;

public sealed class ThemeService
{
    private readonly IDesktopSettings _desktopSettings;

    public ThemePreference CurrentPreference { get; private set; } = ThemePreference.System;

    public ThemeService(IDesktopSettings desktopSettings)
    {
        _desktopSettings = desktopSettings ?? throw new ArgumentNullException(nameof(desktopSettings));
    }

    public void Initialize()
    {
        CurrentPreference = _desktopSettings.GetThemePreference();
        Apply(CurrentPreference);
    }

    public void Set(ThemePreference preference)
    {
        _desktopSettings.SetThemePreference(preference);
        Apply(preference);
        CurrentPreference = preference;
    }

    private static void Apply(ThemePreference preference)
    {
        if (Application.Current is null)
        {
            throw new InvalidOperationException("The Avalonia application has not been initialized.");
        }

        Application.Current.RequestedThemeVariant = preference switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }
}
