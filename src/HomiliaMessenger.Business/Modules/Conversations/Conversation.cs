using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.WebSearch;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HomiliaMessenger.Business.Modules.Conversations;

public readonly record struct ConversationId(Guid Value)
{
    public static ConversationId New()
    {
        return new(Guid.NewGuid());
    }
}

public readonly record struct MessageId(Guid Value)
{
    public static MessageId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>
/// Who wrote a message: the person using the application, or a contact persona (see
/// <see cref="ChatMessage.SenderContactId"/> for which).
/// </summary>
public enum MessageSender
{
    User = 0,
    Contact = 1
}

/// <summary>
/// One message in a conversation.
/// </summary>
public sealed record ChatMessage
{
    public MessageId Id { get; init; }
    public ConversationId ConversationId { get; init; }
    public MessageSender Sender { get; init; }

    /// <summary>
    /// The contact who wrote a contact's message; <see langword="null"/> for the user's messages, and for a contact's
    /// message in a direct conversation saved before groups existed, whose sender is the conversation's contact.
    /// </summary>
    public ContactId? SenderContactId { get; init; }

    public string Text { get; init; } = string.Empty;
    public DateTimeOffset SentAtUtc { get; init; }

    /// <summary>
    /// The agent and model that wrote a contact's reply; <see langword="null"/> for the user's messages.
    /// </summary>
    public AgentAssignment? Agent { get; init; }

    /// <summary>
    /// What the contact looked up online before writing this reply, and where it came from; <see langword="null"/> when
    /// nothing was looked up. Kept for the user to see, and never given to agents or learned as a memory.
    /// </summary>
    public WebLookup? WebSearch { get; init; }

    /// <summary>
    /// A picture sent with the message, such as a contact's selfie; <see langword="null"/> for text alone. A message with
    /// a picture may have no text.
    /// </summary>
    public MessageImage? Image { get; init; }

    /// <summary>
    /// The message as agents read it: its text, after a note of any picture sent with it, since agents cannot see
    /// pictures.
    /// </summary>
    public string AgentText => Image is null
        ? Text
        : string.IsNullOrWhiteSpace(Text) ? $"[{Image.Description}]" : $"[{Image.Description}] {Text}";
}

/// <summary>
/// A picture in a message, as a PNG file, and what it shows in words, for agents and for listing conversations.
/// </summary>
/// <param name="Description">What the picture shows, such as "Sent a selfie: laughing at a bus stop in pouring rain".</param>
public sealed record MessageImage(byte[] Png, string Description);

/// <summary>
/// Whether a conversation is the user's private chat with one contact, or a group chat with several.
/// </summary>
public enum ConversationKind
{
    Direct = 0,
    Group = 1
}

/// <summary>
/// Whether a group's contacts may start a conversation on their own when the user opens the group.
/// </summary>
public enum GroupInitiation
{
    /// <summary>
    /// Contacts write only after the user does (and may then answer each other, within the turn limit).
    /// </summary>
    Off = 0,

    /// <summary>
    /// Now and then, when the group is opened, a contact may also start a conversation.
    /// </summary>
    Occasional = 1
}

/// <summary>
/// A contact taking part in a conversation, with how far what they have learned in it has been condensed into their
/// memories.
/// </summary>
/// <param name="MemoryThrough">
/// The participant's memory checkpoint: the last message whose lasting facts have been learned as this contact's memories;
/// <see langword="null"/> until messages are condensed. Messages after it are given to the contact's agent as they are.
/// </param>
public sealed record ConversationParticipant(ContactId ContactId, MessageId? MemoryThrough = null);

/// <summary>
/// A message history between the user and contacts: either a direct conversation with one contact, which belongs to the
/// contact (a contact has at most one, removed with the contact), or a group with up to
/// <see cref="GroupConversations.MAX_CONTACTS"/> contacts, removed when any of them is.
/// </summary>
public sealed record Conversation
{
    public ConversationId Id { get; init; }
    public ConversationKind Kind { get; init; }

    /// <summary>
    /// The contact of a direct conversation; <see langword="null"/> for a group.
    /// </summary>
    public ContactId? ContactId { get; init; }

    /// <summary>
    /// The contacts taking part, in the order they joined: the one contact of a direct conversation, or a group's
    /// members. The user always takes part, and is not listed.
    /// </summary>
    public IReadOnlyList<ConversationParticipant> Participants { get; init; } = [];

    /// <summary>
    /// A group's own name; <see langword="null"/> to name it after its contacts.
    /// </summary>
    public string? Title { get; init; }

    public DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>
    /// Whether a group's contacts may start conversations when it is opened. Direct conversations never start themselves.
    /// </summary>
    public GroupInitiation Initiation { get; init; } = GroupInitiation.Occasional;

    /// <summary>
    /// When the group last had the chance to start a conversation on its own, whether or not a contact took it, for
    /// spacing those chances out; <see langword="null"/> if it never has.
    /// </summary>
    public DateTimeOffset? LastInitiationAtUtc { get; init; }

    /// <summary>
    /// The most recent message, when listing conversations; <see langword="null"/> for a conversation without messages.
    /// </summary>
    public ChatMessage? LastMessage { get; init; }

    public bool IsGroup => Kind == ConversationKind.Group;

    /// <summary>
    /// The memory checkpoint of a direct conversation's contact (see <see cref="ConversationParticipant.MemoryThrough"/>).
    /// </summary>
    public MessageId? MemoryThrough => Kind == ConversationKind.Direct ? Participants.FirstOrDefault()?.MemoryThrough : null;

    /// <summary>
    /// The IDs of the contacts taking part, in the order they joined.
    /// </summary>
    public IReadOnlyList<ContactId> ContactIds => [.. Participants.Select(participant => participant.ContactId)];

    /// <summary>
    /// When the conversation last changed, for ordering conversations most recent first.
    /// </summary>
    public DateTimeOffset LastActivityAtUtc => LastMessage?.SentAtUtc ?? StartedAtUtc;

    /// <summary>
    /// <paramref name="contactId"/>'s memory checkpoint in this conversation.
    /// </summary>
    public MessageId? MemoryThroughFor(ContactId contactId)
    {
        return Participants.FirstOrDefault(participant => participant.ContactId == contactId)?.MemoryThrough;
    }

    /// <summary>
    /// This conversation with <paramref name="contactId"/>'s memory checkpoint moved to <paramref name="through"/>.
    /// </summary>
    public Conversation WithMemoryThrough(ContactId contactId, MessageId through)
    {
        return this with
        {
            Participants = [.. Participants.Select(participant => participant.ContactId == contactId ? participant with { MemoryThrough = through } : participant)]
        };
    }

    /// <summary>
    /// The contact who wrote <paramref name="message"/>: its recorded sender, or a direct conversation's contact for its
    /// older replies; <see langword="null"/> for the user's messages.
    /// </summary>
    public ContactId? SenderOf(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.Sender == MessageSender.User ? null : message.SenderContactId ?? ContactId;
    }
}
