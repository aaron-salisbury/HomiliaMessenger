using FluentValidation.Results;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Conversations;

/// <summary>
/// The outcome of sending a message. When <see cref="Validation"/> is valid, <see cref="Conversation"/> and
/// <see cref="Message"/> describe what was saved.
/// </summary>
public sealed record SendMessageResult(ValidationResult Validation, Conversation? Conversation, ChatMessage? Message);

/// <summary>
/// The outcome of asking a contact to reply. <see cref="Message"/> is the saved reply on success; <see cref="Problem"/>
/// explains any other status.
/// </summary>
public sealed record ConversationReply(ReplyStatus Status, ChatMessage? Message, string? Problem);

public interface IConversationService
{
    /// <summary>
    /// Lists conversations with their most recent message, most recently active first.
    /// </summary>
    Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken cancellationToken = default);

    Task<Conversation?> FindByContactAsync(ContactId contactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists a conversation's messages, oldest first.
    /// </summary>
    Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(ConversationId conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and saves a message from the user to a contact, starting the contact's conversation if it has none. An
    /// invalid message is not saved, and no conversation is started for it.
    /// </summary>
    Task<SendMessageResult> SendAsync(ContactId contactId, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the contact's agent to reply to the conversation so far, in character, and saves the reply. Contacts without
    /// an assigned agent reply with simulated canned text. The status is <see cref="ReplyStatus.NotFound"/> when the
    /// conversation no longer exists, for example because it was deleted while the contact was replying.
    /// </summary>
    Task<ConversationReply> ReplyAsync(ConversationId conversationId, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(ConversationId conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Persistence capability required by the conversations module.
/// </summary>
public interface IConversationStore
{
    Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken cancellationToken = default);
    Task<Conversation?> GetAsync(ConversationId id, CancellationToken cancellationToken = default);
    Task<Conversation?> FindByContactAsync(ContactId contactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the contact's conversation, inserting <paramref name="conversation"/> when the contact has none yet.
    /// </summary>
    Task<Conversation> GetOrAddAsync(Conversation conversation, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(ConversationId conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a message to its conversation. Returns <see langword="false"/> when the conversation does not exist.
    /// </summary>
    Task<bool> AddMessageAsync(ChatMessage message, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(ConversationId id, CancellationToken cancellationToken = default);
}
