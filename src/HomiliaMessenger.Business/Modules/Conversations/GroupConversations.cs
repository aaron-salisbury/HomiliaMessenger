using HomiliaMessenger.Business.Modules.Contacts;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HomiliaMessenger.Business.Modules.Conversations;

/// <summary>
/// Rules every group conversation follows, kept in one place so a larger limit later is a change here and in the user
/// interface's wording, not throughout.
/// </summary>
public static class GroupConversations
{
    /// <summary>
    /// The most contacts a group may have, besides the user. Storage allows any number.
    /// </summary>
    public const int MAX_CONTACTS = 2;

    /// <summary>
    /// The fewest contacts a group may have: with fewer, it would be a direct conversation.
    /// </summary>
    public const int MIN_CONTACTS = 2;

    /// <summary>
    /// A group's name: its own title when it has one, otherwise its contacts' names, such as "Marcus & Elena".
    /// </summary>
    public static string DisplayName(Conversation conversation, IEnumerable<Contact> contacts)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(contacts);

        if (!string.IsNullOrWhiteSpace(conversation.Title))
        {
            return conversation.Title.Trim();
        }

        Dictionary<ContactId, Contact> byId = contacts.GroupBy(contact => contact.Id).ToDictionary(group => group.Key, group => group.First());
        List<string> names = [.. conversation.ContactIds.Select(id => byId.TryGetValue(id, out Contact? contact) ? contact.DisplayName : null).OfType<string>().Where(name => name.Length > 0)];

        return names.Count switch
        {
            0 => "Group",
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " & " + names[^1]
        };
    }
}

/// <summary>
/// What started a round of group replies.
/// </summary>
public enum GroupTrigger
{
    /// <summary>
    /// The user sent a message.
    /// </summary>
    UserMessage,

    /// <summary>
    /// The user opened the group, which may let a contact start a conversation (see <see cref="GroupInitiation"/>).
    /// </summary>
    Opened
}

/// <summary>
/// Why a contact is writing next, which shapes what they are asked to write.
/// </summary>
public enum GroupTurnReason
{
    /// <summary>
    /// Someone spoke to them by name.
    /// </summary>
    Addressed,

    /// <summary>
    /// Answering the user's latest message.
    /// </summary>
    Answering,

    /// <summary>
    /// Adding to, or reacting to, what another contact just said.
    /// </summary>
    FollowingUp,

    /// <summary>
    /// Saying hello in a group just created.
    /// </summary>
    Greeting,

    /// <summary>
    /// Starting a new conversation unprompted, when the user opened the group.
    /// </summary>
    Initiating
}

/// <summary>
/// One round of group replies: what started it, and how many contacts have written in it so far. Rounds are bounded by
/// <see cref="GroupCoordinatorOptions.MaxAutomaticTurns"/>, after which the group waits for the user.
/// </summary>
/// <param name="ModelConsulted">Whether a contact's model has already been asked whether to add something in this round.</param>
public sealed record GroupCycle(GroupTrigger Trigger, int TurnsTaken = 0, bool ModelConsulted = false)
{
    /// <summary>
    /// Contacts who could not reply in this round, such as because their model is not downloaded, and are not asked
    /// again in it, so the others can still answer.
    /// </summary>
    public IReadOnlyList<ContactId> Unavailable { get; init; } = [];

    /// <summary>
    /// The round after a contact could not write their turn. It still counts towards the round's limit.
    /// </summary>
    public GroupCycle AfterFailure(GroupTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        return this with { TurnsTaken = TurnsTaken + 1, Unavailable = [.. Unavailable, turn.Speaker] };
    }

    /// <summary>
    /// The round after a contact wrote in it.
    /// </summary>
    public GroupCycle After(GroupTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        return this with { TurnsTaken = TurnsTaken + 1, ModelConsulted = ModelConsulted || turn.ModelConsulted };
    }
}

/// <summary>
/// Who writes next in a group, and why.
/// </summary>
/// <param name="ModelConsulted">Whether the speaker's model was asked whether they would write, to decide this turn.</param>
public sealed record GroupTurn(ContactId Speaker, GroupTurnReason Reason, bool ModelConsulted = false);

/// <summary>
/// The limits and likelihoods that shape group conversations. The composition root may register different values.
/// </summary>
public sealed record GroupCoordinatorOptions
{
    /// <summary>
    /// The most messages contacts write in one round before waiting for the user.
    /// </summary>
    public int MaxAutomaticTurns { get; init; } = 3;

    /// <summary>
    /// How long after a group last had the chance to start a conversation on its own before it has another, whether or
    /// not a contact took it, so opening the group repeatedly does not bring a stream of unprompted messages.
    /// </summary>
    public TimeSpan InitiationCooldown { get; init; } = TimeSpan.FromHours(4);

    /// <summary>
    /// How quiet a group must have been before a contact starts a new conversation in it.
    /// </summary>
    public TimeSpan InitiationQuietPeriod { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The chance a contact starts a conversation when the group is eligible.
    /// </summary>
    public double InitiationChance { get; init; } = 0.5;

    /// <summary>
    /// The base chance another contact adds something nobody asked them for, before personality and participation.
    /// </summary>
    public double FollowUpChance { get; init; } = 0.4;

    /// <summary>
    /// How many recent messages are weighed for who has been doing the talking.
    /// </summary>
    public int ParticipationWindow { get; init; } = 12;
}
