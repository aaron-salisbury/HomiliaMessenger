using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomiliaMessenger.Presentation.Desktop.Settings;

internal sealed class DesktopSettings : IDesktopSettings
{
    private const string SETTINGS_FILE_NAME = "desktop-settings.json";

    private static readonly JsonSerializerOptions SERIALIZER_OPTIONS = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public DesktopSettings(string applicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        _settingsPath = Path.Combine(applicationDataDirectory, SETTINGS_FILE_NAME);
    }

    public ThemePreference GetThemePreference()
    {
        return Read().Theme;
    }

    public void SetThemePreference(ThemePreference preference)
    {
        DesktopSettingsData settings = Read();
        settings.Theme = preference;
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, SERIALIZER_OPTIONS));
    }

    private DesktopSettingsData Read()
    {
        if (!File.Exists(_settingsPath))
        {
            return new DesktopSettingsData();
        }

        try
        {
            string json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<DesktopSettingsData>(json, SERIALIZER_OPTIONS)
                ?? new DesktopSettingsData();
        }
        catch (JsonException)
        {
            return new DesktopSettingsData();
        }
    }

    private sealed class DesktopSettingsData
    {
        public ThemePreference Theme { get; set; } = ThemePreference.System;
    }
}
