using FluentValidation;
using FluentValidation.Results;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Memories;
using HomiliaMessenger.Business.Modules.Profile;
using HomiliaMessenger.Business.Modules.WebSearch;
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
    private readonly IUserProfileService _profile;
    private readonly IValidator<ChatMessage> _validator;
    private readonly IReadOnlyList<IConversationAgent> _agents;
    private readonly IMemoryStore _memoryStore;
    private readonly IMemoryService _memories;
    private readonly MemoryRecall _recall;
    private readonly MemoryRecallOptions _recallOptions;
    private readonly WebSearchService _webSearch;
    private readonly TimeProvider _timeProvider;

    public ConversationService(IConversationStore store, IContactService contacts, IUserProfileService profile, IValidator<ChatMessage> validator, IEnumerable<IConversationAgent> agents, IMemoryStore memoryStore, IMemoryService memories, MemoryRecall recall, MemoryRecallOptions recallOptions, WebSearchService webSearch)
        : this(store, contacts, profile, validator, agents, memoryStore, memories, recall, recallOptions, webSearch, TimeProvider.System)
    {
    }

    internal ConversationService(IConversationStore store, IContactService contacts, IUserProfileService profile, IValidator<ChatMessage> validator, IEnumerable<IConversationAgent> agents, IMemoryStore memoryStore, IMemoryService memories, MemoryRecall recall, MemoryRecallOptions recallOptions, WebSearchService webSearch, TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        ArgumentNullException.ThrowIfNull(agents);
        _agents = [.. agents];
        _memoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
        _memories = memories ?? throw new ArgumentNullException(nameof(memories));
        _recall = recall ?? throw new ArgumentNullException(nameof(recall));
        _recallOptions = recallOptions ?? throw new ArgumentNullException(nameof(recallOptions));
        _webSearch = webSearch ?? throw new ArgumentNullException(nameof(webSearch));
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

    public async Task<ConversationReply> ReplyAsync(ConversationId conversationId, IProgress<ReplyActivity>? activity = null, CancellationToken cancellationToken = default)
    {
        Conversation? conversation = await _store.GetAsync(conversationId, cancellationToken);
        Contact? contact = conversation is null ? null : await _contacts.GetAsync(conversation.ContactId, cancellationToken);

        if (conversation is null || contact is null)
        {
            return new ConversationReply(ReplyStatus.NotFound, null, "The conversation no longer exists.");
        }

        AgentAssignment assignment = contact.Agent ?? SimulatedAgent.ASSIGNMENT;
        IConversationAgent? agent = FindAgent(assignment);

        if (agent is null)
        {
            string known = string.Join(", ", _agents.Select(candidate => candidate.Provider).Distinct(StringComparer.OrdinalIgnoreCase));
            return new ConversationReply(ReplyStatus.Unavailable, null, $"There is no agent provider named \"{assignment.Provider}\". Available providers: {known}.");
        }

        IReadOnlyList<ChatMessage> messages = await _store.ListMessagesAsync(conversationId, cancellationToken);
        UserProfile user = await _profile.GetAsync(cancellationToken);
        DateOnly today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);

        // The agent is given the memories recalled for the latest messages, and the messages after the memory checkpoint.
        int unremembered = MemoryNotes.FirstUnremembered(messages, conversation.MemoryThrough);
        IReadOnlyList<ChatMessage> recent = [.. messages.Skip(unremembered)];
        IReadOnlyList<Memory> recalled = agent is SimulatedAgent
            ? []
            : await _recall.RecallAsync(contact.Id, MemoryRecall.QueryFor(recent, _recallOptions.MaxQueryCharacters), cancellationToken);
        // Canned replies could make no use of what was found. Only the latest messages decide, and only a short query
        // made from them is sent; the results inform this reply alone and are kept with it for the user to see.
        WebLookup? lookup = agent is SimulatedAgent
            ? null
            : await _webSearch.LookUpForReplyAsync(recent, PrivateNames(contact, user), agent.ReplyAsync, assignment.Model.Trim(), () => activity?.Report(ReplyActivity.SearchingWeb), cancellationToken);
        activity?.Report(ReplyActivity.Writing);

        AgentRequest request = PersonaFraming.Build(contact, user, [.. recalled.Select(memory => memory.Text)], recent, assignment.Model.Trim(), today, lookup?.Sources);
        AgentReply answer = await agent.ReplyAsync(request, cancellationToken);

        if (answer.Status != ReplyStatus.Success)
        {
            return new ConversationReply(answer.Status, null, answer.Problem);
        }

        string? text = PersonaFraming.CleanReply(answer.Text, contact, request);

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
            Agent = assignment,
            WebSearch = lookup
        };

        return await _store.AddMessageAsync(reply, cancellationToken)
            ? new ConversationReply(ReplyStatus.Success, reply, null)
            : new ConversationReply(ReplyStatus.NotFound, null, "The conversation was deleted before the reply was saved.");
    }

    public async Task<Conversation?> UpdateMemoryAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        Conversation? conversation = await _store.GetAsync(conversationId, cancellationToken);
        Contact? contact = conversation is null ? null : await _contacts.GetAsync(conversation.ContactId, cancellationToken);

        // Canned replies make no use of memories, and have no model to list them.
        if (conversation is null || contact?.Agent is not { } assignment || FindAgent(assignment) is not { } agent || agent is SimulatedAgent)
        {
            return null;
        }

        Conversation? updated = await LearnAsync(conversation, contact, assignment, agent, cancellationToken);

        // Vectors for what was just learned, and for any memories still waiting: added or corrected by the user, not
        // embedded after an earlier failure, or embedded by a model no longer chosen.
        await _memories.EmbedPendingAsync(contact.Id, cancellationToken);

        return updated;
    }

    /// <summary>
    /// Lists the facts in the next batch of messages after the checkpoint, and saves each new one as a memory as the
    /// checkpoint moves past the batch. Returns <see langword="null"/> when there was nothing to condense or nothing was
    /// saved; the batch is then tried again after the next reply.
    /// </summary>
    private async Task<Conversation?> LearnAsync(Conversation conversation, Contact contact, AgentAssignment assignment, IConversationAgent agent, CancellationToken cancellationToken)
    {
        IReadOnlyList<ChatMessage> messages = await _store.ListMessagesAsync(conversation.Id, cancellationToken);
        int start = MemoryNotes.FirstUnremembered(messages, conversation.MemoryThrough);

        if (MemoryNotes.FoldBoundary(messages, start) is not { } boundary)
        {
            return null;
        }

        IReadOnlyList<ChatMessage> batch = [.. messages.Take(boundary).Skip(start)];
        UserProfile user = await _profile.GetAsync(cancellationToken);

        // What is already known, as far as it bears on this batch, so the agent lists only what is new.
        IReadOnlyList<Memory> known = await _recall.RecallAsync(contact.Id, MemoryRecall.QueryForBatch(batch, _recallOptions.MaxQueryCharacters), cancellationToken);
        AgentRequest request = MemoryNotes.ExtractRequest(contact, user, [.. known.Select(memory => memory.Text)], batch, assignment.Model.Trim());
        AgentReply answer = await agent.ReplyAsync(request, cancellationToken);

        if (answer.Status != ReplyStatus.Success)
        {
            // Try again after the next reply; until then the agent is still given the messages themselves.
            return null;
        }

        MessageId through = messages[boundary - 1].Id;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        IReadOnlyList<Memory> existing = await _memoryStore.ListAsync(contact.Id, cancellationToken);
        List<Memory> learned = [];

        foreach (string fact in MemoryNotes.Facts(answer.Text))
        {
            if (existing.Any(memory => MemoryNotes.IsSameFact(memory.Text, fact)) || learned.Any(memory => MemoryNotes.IsSameFact(memory.Text, fact)))
            {
                continue;
            }

            learned.Add(new Memory
            {
                Id = MemoryId.New(),
                ContactId = contact.Id,
                ConversationId = conversation.Id,
                Text = fact,
                Origin = MemoryOrigin.Extracted,
                SourceMessageId = through,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }

        // The memories and the checkpoint are saved together, so the checkpoint never moves past facts that were not
        // saved. Finding nothing worth remembering still moves it past these messages. If another update moved the
        // checkpoint meanwhile, or the conversation was deleted, nothing is saved.
        bool saved = await _memoryStore.AddExtractedAsync(conversation.Id, learned, through, conversation.MemoryThrough, cancellationToken);

        return saved ? conversation with { MemoryThrough = through } : null;
    }

    /// <summary>
    /// Names that identify the user or the contact, which are never sent in a search query.
    /// </summary>
    private static IReadOnlyCollection<string> PrivateNames(Contact contact, UserProfile user)
    {
        return [.. new[] { contact.DisplayName, contact.GivenName, contact.FamilyName, contact.Nickname, user.Name }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())];
    }

    /// <summary>
    /// The agent for <paramref name="assignment"/>. Later registrations take precedence, as they do for single services.
    /// </summary>
    private IConversationAgent? FindAgent(AgentAssignment assignment)
    {
        return _agents.LastOrDefault(candidate => string.Equals(candidate.Provider, assignment.Provider.Trim(), StringComparison.OrdinalIgnoreCase));
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
