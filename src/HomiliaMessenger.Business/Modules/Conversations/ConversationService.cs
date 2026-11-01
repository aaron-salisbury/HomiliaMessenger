using FluentValidation;
using FluentValidation.Results;
using HomiliaMessenger.Business.Modules.Contacts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Conversations;

internal sealed class ConversationService : IConversationService
{
    private readonly IConversationStore _store;
    private readonly IContactService _contacts;
    private readonly IValidator<ChatMessage> _validator;
    private readonly SimulatedReplies _replies;
    private readonly TimeProvider _timeProvider;

    public ConversationService(IConversationStore store, IContactService contacts, IValidator<ChatMessage> validator, SimulatedReplies replies)
        : this(store, contacts, validator, replies, TimeProvider.System)
    {
    }

    internal ConversationService(IConversationStore store, IContactService contacts, IValidator<ChatMessage> validator, SimulatedReplies replies, TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _replies = replies ?? throw new ArgumentNullException(nameof(replies));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken cancellationToken = default)
    {
        return _store.ListAsync(cancellationToken);
    }

    public Task<Conversation?> FindByContactAsync(ContactId contactId, CancellationToken cancellationToken = default)
    {
        return _store.FindByContactAsync(contactId, cancellationToken);
    }

    public Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        return _store.ListMessagesAsync(conversationId, cancellationToken);
    }

    public async Task<SendMessageResult> SendAsync(ContactId contactId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (await _contacts.GetAsync(contactId, cancellationToken) is null)
        {
            ValidationResult missingContact = new([new ValidationFailure("Contact", "The contact no longer exists.")]);
            return new SendMessageResult(missingContact, null, null);
        }

        Conversation? existing = await _store.FindByContactAsync(contactId, cancellationToken);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        ChatMessage message = new()
        {
            Id = MessageId.New(),
            ConversationId = existing?.Id ?? ConversationId.New(),
            Sender = MessageSender.User,
            Text = text.Trim(),
            SentAtUtc = now
        };

        // Validate before starting a conversation, so an invalid first message leaves nothing behind.
        ValidationResult result = await _validator.ValidateAsync(message, cancellationToken);

        if (!result.IsValid)
        {
            return new SendMessageResult(result, null, null);
        }

        Conversation conversation = existing ?? await _store.GetOrAddAsync(new Conversation
        {
            Id = message.ConversationId,
            ContactId = contactId,
            StartedAtUtc = now
        }, cancellationToken);

        message = message with { ConversationId = conversation.Id };

        if (!await _store.AddMessageAsync(message, cancellationToken))
        {
            ValidationResult deleted = new([new ValidationFailure("Conversation", "The conversation was deleted before the message was saved.")]);
            return new SendMessageResult(deleted, null, null);
        }

        return new SendMessageResult(result, conversation with { LastMessage = message }, message);
    }

    public async Task<ChatMessage?> ReplyAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        if (await _store.GetAsync(conversationId, cancellationToken) is null)
        {
            return null;
        }

        string text = await _replies.NextAsync(cancellationToken);

        ChatMessage reply = new()
        {
            Id = MessageId.New(),
            ConversationId = conversationId,
            Sender = MessageSender.Contact,
            Text = text,
            SentAtUtc = _timeProvider.GetUtcNow()
        };

        return await _store.AddMessageAsync(reply, cancellationToken) ? reply : null;
    }

    public Task<bool> DeleteAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        if (conversationId.Value == Guid.Empty)
        {
            throw new ArgumentException("A conversation ID is required.", nameof(conversationId));
        }

        return _store.DeleteAsync(conversationId, cancellationToken);
    }
}
