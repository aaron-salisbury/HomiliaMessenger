using HomiliaMessenger.Business.Modules.Contacts;
using System;

namespace HomiliaMessenger.Business.Modules.Profile;

/// <summary>
/// Who the person using the application is, so contacts know who they are talking to. Every field is optional; there is
/// one profile per installation.
/// </summary>
public sealed record UserProfile
{
    public const int MAX_ABOUT_LENGTH = 2000;

    public string? Name { get; init; }
    public DateOnly? Birthday { get; init; }
    public Gender? Gender { get; init; }

    /// <summary>
    /// What contacts should know about the user, such as where they live, their work, or their interests.
    /// </summary>
    public string? About { get; init; }

    public byte[]? Photo { get; init; }

    /// <summary>
    /// The user's age in whole years as of <paramref name="today"/>, or <see langword="null"/> without a birthday.
    /// </summary>
    public int? AgeOn(DateOnly today)
    {
        return Birthday is null ? null : ContactBirthday.AgeOn(Birthday.Value, today);
    }
}
