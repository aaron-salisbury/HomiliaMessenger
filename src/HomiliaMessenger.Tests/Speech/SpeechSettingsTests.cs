using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Presentation.Desktop.Settings;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Speech;

[TestClass]
public sealed class SpeechSettingsTests
{
    [TestMethod]
    public async Task Settings_DownloadsEnablesDisablesAndRemovesKokoro()
    {
        string directory = Path.Combine(Path.GetTempPath(), "HomiliaSpeech-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            FakeSpeech speech = new() { Model = new("Kokoro", 100, false, false) };
            SettingsViewModel settings = new(new ThemeService(new DesktopSettings(directory)), new EmptyLocalModels(), speech)
            {
                ConfirmAsync = (_, _) => Task.FromResult(true)
            };
            await settings.InitializeAsync();
            Assert.IsTrue(settings.CanDownloadSpeech);
            await settings.DownloadSpeechCommand.ExecuteAsync(null);
            Assert.IsTrue(settings.HasSpeechModel);
            Assert.IsTrue(settings.SpeechEnabled);
            Assert.IsFalse(settings.CanDownloadSpeech);
            await settings.ToggleSpeechModelCommand.ExecuteAsync(null);
            Assert.IsFalse(settings.SpeechEnabled);
            Assert.IsTrue(settings.HasSpeechModel);
            await settings.ToggleSpeechModelCommand.ExecuteAsync(null);
            Assert.IsTrue(settings.SpeechEnabled);
            await settings.RemoveSpeechModelCommand.ExecuteAsync(null);
            Assert.IsFalse(settings.HasSpeechModel);
            Assert.IsFalse(settings.SpeechEnabled);
            Assert.IsTrue(settings.CanDownloadSpeech);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task Settings_CancelingADownloadLeavesSpeechUnavailableAndCanRetry()
    {
        FakeSpeech speech = new() { Model = new("Kokoro", 100, false, false), HoldDownload = true };
        SettingsViewModel settings = new(new ThemeService(new DesktopSettings(Path.GetTempPath())), new EmptyLocalModels(), speech);
        Task download = settings.DownloadSpeechCommand.ExecuteAsync(null);
        Assert.IsTrue(settings.IsSpeechDownloading);
        settings.CancelSpeechDownloadCommand.Execute(null);
        await download;
        Assert.IsFalse(settings.IsSpeechDownloading);
        Assert.IsFalse(settings.HasSpeechModel);
        Assert.IsFalse(settings.SpeechEnabled);
        speech.HoldDownload = false;
        await settings.DownloadSpeechCommand.ExecuteAsync(null);
        Assert.IsTrue(settings.SpeechEnabled);
    }

    private sealed class EmptyLocalModels : ILocalModelLibrary
    {
        public string Provider => "Local";
        public IReadOnlyList<LocalModel> List()
        {
            return [];
        }
        public Task<LocalModelDownloadResult> DownloadAsync(string modelId, IProgress<LocalModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
        public Task<bool> DeleteAsync(string modelId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
