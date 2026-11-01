using FluentValidation;
using FluentValidation.Results;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Conversations;

internal sealed class ConversationService : IConversationService
{
    private readonly IConversationStore _store;
    private readonly IContactService _contacts;
    private readonly IValidator<ChatMessage> _validator;
    private readonly IReadOnlyList<IConversationAgent> _agents;
    private readonly TimeProvider _timeProvider;

    public ConversationService(IConversationStore store, IContactService contacts, IValidator<ChatMessage> validator, IEnumerable<IConversationAgent> agents)
        : this(store, contacts, validator, agents, TimeProvider.System)
    {
    }

    internal ConversationService(IConversationStore store, IContactService contacts, IValidator<ChatMessage> validator, IEnumerable<IConversationAgent> agents, TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        ArgumentNullException.ThrowIfNull(agents);
        _agents = [.. agents];
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

    public async Task<ConversationReply> ReplyAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        Conversation? conversation = await _store.GetAsync(conversationId, cancellationToken);
        Contact? contact = conversation is null ? null : await _contacts.GetAsync(conversation.ContactId, cancellationToken);

        if (conversation is null || contact is null)
        {
            return new ConversationReply(ReplyStatus.NotFound, null, "The conversation no longer exists.");
        }

        AgentAssignment assignment = contact.Agent ?? SimulatedAgent.ASSIGNMENT;

        // Later registrations take precedence, as they do for single services.
        IConversationAgent? agent = _agents.LastOrDefault(candidate => string.Equals(candidate.Provider, assignment.Provider.Trim(), StringComparison.OrdinalIgnoreCase));

        if (agent is null)
        {
            string known = string.Join(", ", _agents.Select(candidate => candidate.Provider).Distinct(StringComparer.OrdinalIgnoreCase));
            return new ConversationReply(ReplyStatus.Unavailable, null, $"There is no agent provider named \"{assignment.Provider}\". Available providers: {known}.");
        }

        IReadOnlyList<ChatMessage> messages = await _store.ListMessagesAsync(conversationId, cancellationToken);
        DateOnly today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        AgentRequest request = PersonaFraming.Build(contact, messages, assignment.Model.Trim(), today);
        AgentReply answer = await agent.ReplyAsync(request, cancellationToken);

        if (answer.Status != ReplyStatus.Success)
        {
            return new ConversationReply(answer.Status, null, answer.Problem);
        }

        string? text = PersonaFraming.CleanReply(answer.Text, contact);

        if (text is null)
        {
            return new ConversationReply(ReplyStatus.InvalidResponse, null, "The agent replied with nothing usable.");
        }

        if (text.Length > ChatMessageValidator.MAX_TEXT_LENGTH)
        {
            text = text[..ChatMessageValidator.MAX_TEXT_LENGTH];
        }

        ChatMessage reply = new()
        {
            Id = MessageId.New(),
            ConversationId = conversationId,
            Sender = MessageSender.Contact,
            Text = text,
            SentAtUtc = _timeProvider.GetUtcNow(),
            Agent = assignment
        };

        return await _store.AddMessageAsync(reply, cancellationToken)
            ? new ConversationReply(ReplyStatus.Success, reply, null)
            : new ConversationReply(ReplyStatus.NotFound, null, "The conversation was deleted before the reply was saved.");
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
