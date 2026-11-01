using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Speech;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Speech;

internal sealed class FakeSpeech : ITextToSpeech
{
    public SpeechModel Model { get; set; } = new("Kokoro", 100, true, true);
    public IReadOnlyList<SpeechVoice> Voices { get; } =
    [
        new("af_heart", "Heart", Gender.Female),
        new("am_adam", "Adam", Gender.Male)
    ];
    public List<(string Text, string Voice)> Spoken { get; } = [];
    public bool HoldPlayback { get; set; }
    public bool FailPlayback { get; set; }
    public bool HoldDownload { get; set; }
    public bool FailEnable { get; set; }
    public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<SpeechResult> DownloadAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (HoldDownload)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        Model = Model with { IsDownloaded = true };
        progress?.Report(100);
        return SpeechResult.Success;
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (FailEnable)
        {
            throw new IOException("The settings folder is read-only.");
        }

        Model = Model with { IsEnabled = enabled };
        return Task.CompletedTask;
    }

    public Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        Model = Model with { IsDownloaded = false, IsEnabled = false };
        return Task.CompletedTask;
    }

    public async Task<SpeechResult> SpeakAsync(string text, string voiceId, CancellationToken cancellationToken = default)
    {
        Spoken.Add((text, voiceId));
        if (HoldPlayback)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Canceled.TrySetResult();
                throw;
            }
        }

        return FailPlayback ? new(false, "Audio device unavailable.") : SpeechResult.Success;
    }
}
