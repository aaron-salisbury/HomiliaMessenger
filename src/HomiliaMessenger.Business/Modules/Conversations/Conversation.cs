using HomiliaMessenger.Business.Modules.Contacts;
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
    /// The most recent message, when listing conversations; <see langword="null"/> for a conversation without messages.
    /// </summary>
    public ChatMessage? LastMessage { get; init; }

    /// <summary>
    /// When the conversation last changed, for ordering conversations most recent first.
    /// </summary>
    public DateTimeOffset LastActivityAtUtc => LastMessage?.SentAtUtc ?? StartedAtUtc;
}
