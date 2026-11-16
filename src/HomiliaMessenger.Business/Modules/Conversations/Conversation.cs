using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.WebSearch;
using System;

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
/// Who wrote a message: the person using the application, or the contact persona.
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
}

/// <summary>
/// The ongoing message history between the user and one contact. A conversation belongs to the contact, so a contact has
/// at most one, and it is removed with the contact.
/// </summary>
public sealed record Conversation
{
    public ConversationId Id { get; init; }
    public ContactId ContactId { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>
    /// The memory checkpoint: the last message whose lasting facts have been learned as the contact's memories;
    /// <see langword="null"/> until messages are condensed. Messages after it are given to agents as they are.
    /// </summary>
    public MessageId? MemoryThrough { get; init; }

    /// <summary>
    /// The most recent message, when listing conversations; <see langword="null"/> for a conversation without messages.
    /// </summary>
    public ChatMessage? LastMessage { get; init; }

    /// <summary>
    /// When the conversation last changed, for ordering conversations most recent first.
    /// </summary>
    public DateTimeOffset LastActivityAtUtc => LastMessage?.SentAtUtc ?? StartedAtUtc;
}
