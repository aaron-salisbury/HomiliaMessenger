using HomiliaMessenger.Business.Modules.Contacts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Speech;

public sealed record SpeechVoice(string Id, string DisplayName, Gender Gender)
{
    public override string ToString()
    {
        return DisplayName;
    }
}

public sealed record SpeechModel(string DisplayName, long DownloadBytes, bool IsDownloaded, bool IsEnabled)
{
    public bool IsAvailable => IsDownloaded && IsEnabled;
}

public sealed record SpeechResult(bool IsSuccessful, string? Problem = null)
{
    public static SpeechResult Success { get; } = new(true);
}

/// <summary>Optional local speech. Only model setup and contact voice choices persist; playback consent does not.</summary>
public interface ITextToSpeech
{
    SpeechModel Model { get; }
    IReadOnlyList<SpeechVoice> Voices { get; }
    Task<SpeechResult> DownloadAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task RemoveAsync(CancellationToken cancellationToken = default);
    /// <summary>Speaks one new reply. Cancellation stops playback and discards any remaining segments.</summary>
    Task<SpeechResult> SpeakAsync(string text, string voiceId, CancellationToken cancellationToken = default);
}
