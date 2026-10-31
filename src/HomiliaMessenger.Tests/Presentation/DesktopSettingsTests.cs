using HomiliaMessenger.Presentation.Desktop.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace HomiliaMessenger.Tests.Presentation;

[TestClass]
public sealed class DesktopSettingsTests
{
    [TestMethod]
    public void ThemePreference_DefaultsToSystemAndPersistsAcrossInstances()
    {
        string directory = CreateDirectory();

        try
        {
            Assert.AreEqual(ThemePreference.System, new DesktopSettings(directory).GetThemePreference());

            new DesktopSettings(directory).SetThemePreference(ThemePreference.Dark);

            Assert.AreEqual(ThemePreference.Dark, new DesktopSettings(directory).GetThemePreference());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ThemePreference_FallsBackToSystemWhenSettingsFileIsCorrupt()
    {
        string directory = CreateDirectory();

        try
        {
            File.WriteAllText(Path.Combine(directory, "desktop-settings.json"), "{ not json");

            Assert.AreEqual(ThemePreference.System, new DesktopSettings(directory).GetThemePreference());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"HomiliaMessenger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        return directory;
    }
}
