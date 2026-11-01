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
            settings.SpeechEnabled = false;
            await WaitUntilAsync(() => !speech.Model.IsEnabled);
            Assert.IsFalse(settings.SpeechEnabled);
            Assert.IsTrue(settings.HasSpeechModel);
            settings.SpeechEnabled = true;
            await WaitUntilAsync(() => speech.Model.IsEnabled);
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
    public async Task Settings_UseKokoroReturnsToTheActualStateWhenItCannotChange()
    {
        FakeSpeech speech = new() { Model = new("Kokoro", 100, true, false), FailEnable = true };
        SettingsViewModel settings = new(new ThemeService(new DesktopSettings(Path.GetTempPath())), new EmptyLocalModels(), speech);
        await settings.InitializeAsync();

        settings.SpeechEnabled = true;
        await WaitUntilAsync(() => !settings.SpeechEnabled);

        Assert.IsFalse(speech.Model.IsEnabled);
        Assert.Contains("read-only", settings.SpeechStatus);

        FakeSpeech missing = new() { Model = new("Kokoro", 100, false, false) };
        SettingsViewModel withoutModel = new(new ThemeService(new DesktopSettings(Path.GetTempPath())), new EmptyLocalModels(), missing);
        await withoutModel.InitializeAsync();
        withoutModel.SpeechEnabled = true;
        await WaitUntilAsync(() => !withoutModel.SpeechEnabled);
        Assert.IsFalse(missing.Model.IsEnabled);
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition(), "Timed out waiting for the settings to update.");
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
        public Task<HuggingFaceLookup> LookUpAsync(string repository, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
        public Task<LocalModelAddResult> AddAsync(HuggingFaceLookup lookup, GgufVariant variant, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
        public Task<bool> HasHuggingFaceTokenAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }
        public Task SetHuggingFaceTokenAsync(string? token, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
