using HomiliaMessenger.Business.Modules.Contacts;

namespace HomiliaMessenger.Presentation.Desktop.Models;

public sealed record VoiceOption(string Label, string? Id, Gender? Gender = null)
{
    public static VoiceOption None { get; } = new("None (text only)", null);

    public override string ToString()
    {
        return Label;
    }
}
