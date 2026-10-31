using System;
using System.Linq;

namespace HomiliaMessenger.Business.Modules.Contacts;

public readonly record struct ContactId(Guid Value)
{
    public static ContactId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>
/// Identifies the agent that speaks for a contact: the provider the application connects through and the model it uses.
/// </summary>
/// <remarks>
/// Providers are not integrated yet, so both values are user-entered identifiers. Changing a contact's agent keeps the
/// contact, and later its conversation history, intact.
/// </remarks>
public sealed record AgentAssignment(string Provider, string Model);

/// <summary>
/// A messenger contact. Every contact is an AI persona; its persona fields describe who the backing agent should be.
/// </summary>
/// <remarks>
/// A contact is an application-facing snapshot. Data rows and UI editing state live in their own layers.
/// </remarks>
public sealed record Contact
{
    public ContactId Id { get; init; }
    public string? GivenName { get; init; }
    public string? FamilyName { get; init; }
    public string? Nickname { get; init; }
    /// <summary>
    /// Required when saving. Contacts must be at least 18 years old as of the UTC date.
    /// </summary>
    public DateOnly? Birthday { get; init; }
    public string? Gender { get; init; }

    /// <summary>
    /// Temperament, values, and how the persona carries a conversation.
    /// </summary>
    public string? Personality { get; init; }

    /// <summary>
    /// Life history, relationships, and experiences the persona can draw from.
    /// </summary>
    public string? Background { get; init; }

    public AgentAssignment? Agent { get; init; }
    public byte[]? Photo { get; init; }
    public bool IsFavorite { get; init; }

    /// <summary>
    /// The contact's age in whole years as of <paramref name="today"/>, or <see langword="null"/> without a birthday.
    /// </summary>
    public int? AgeOn(DateOnly today)
    {
        return Birthday is null ? null : ContactBirthday.AgeOn(Birthday.Value, today);
    }

    public string DisplayName => !string.IsNullOrWhiteSpace(Nickname)
        ? Nickname
        : string.Join(" ", new[] { GivenName, FamilyName }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
