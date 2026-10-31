namespace HomiliaMessenger.Presentation.Desktop.Settings;

public interface IDesktopSettings
{
    ThemePreference GetThemePreference();

    void SetThemePreference(ThemePreference preference);
}
