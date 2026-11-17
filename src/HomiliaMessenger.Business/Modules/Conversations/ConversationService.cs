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
    private readonly GroupCoordinator _coordinator;
    private readonly TimeProvider _timeProvider;

    public ConversationService(IConversationStore store, IContactService contacts, IUserProfileService profile, IValidator<ChatMessage> validator, IEnumerable<IConversationAgent> agents, IMemoryStore memoryStore, IMemoryService memories, MemoryRecall recall, MemoryRecallOptions recallOptions, WebSearchService webSearch, GroupCoordinator coordinator)
        : this(store, contacts, profile, validator, agents, memoryStore, memories, recall, recallOptions, webSearch, coordinator, TimeProvider.System)
    {
    }

    internal ConversationService(IConversationStore store, IContactService contacts, IUserProfileService profile, IValidator<ChatMessage> validator, IEnumerable<IConversationAgent> agents, IMemoryStore memoryStore, IMemoryService memories, MemoryRecall recall, MemoryRecallOptions recallOptions, WebSearchService webSearch, GroupCoordinator coordinator, TimeProvider timeProvider)
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
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken cancellationToken = default)
    {
        return _store.ListAsync(cancellationToken);
    }

    public Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        return _store.GetAsync(conversationId, cancellationToken);
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
            return Failed("Contact", "The contact no longer exists.");
        }

        Conversation? existing = await _store.FindByContactAsync(contactId, cancellationToken);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        ChatMessage message = UserMessage(existing?.Id ?? ConversationId.New(), text, now);

        // Validate before starting a conversation, so an invalid first message leaves nothing behind.
        ValidationResult result = await _validator.ValidateAsync(message, cancellationToken);

        if (!result.IsValid)
        {
            return new SendMessageResult(result, null, null);
        }

        Conversation conversation = existing ?? await _store.GetOrAddAsync(new Conversation
        {
            Id = message.ConversationId,
            Kind = ConversationKind.Direct,
            ContactId = contactId,
            Participants = [new ConversationParticipant(contactId)],
            StartedAtUtc = now
        }, cancellationToken);

        return await SaveAsync(conversation, message with { ConversationId = conversation.Id }, result, cancellationToken);
    }

    public async Task<SendMessageResult> SendAsync(ConversationId conversationId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (await _store.GetAsync(conversationId, cancellationToken) is not { } conversation)
        {
            return Failed("Conversation", "The conversation no longer exists.");
        }

        ChatMessage message = UserMessage(conversationId, text, _timeProvider.GetUtcNow());
        ValidationResult result = await _validator.ValidateAsync(message, cancellationToken);

        return result.IsValid
            ? await SaveAsync(conversation, message, result, cancellationToken)
            : new SendMessageResult(result, null, null);
    }

    private static ChatMessage UserMessage(ConversationId conversationId, string text, DateTimeOffset now)
    {
        return new()
        {
            Id = MessageId.New(),
            ConversationId = conversationId,
            Sender = MessageSender.User,
            Text = text.Trim(),
            SentAtUtc = now
        };
    }

    private async Task<SendMessageResult> SaveAsync(Conversation conversation, ChatMessage message, ValidationResult result, CancellationToken cancellationToken)
    {
        if (!await _store.AddMessageAsync(message, cancellationToken))
        {
            return Failed("Conversation", "The conversation was deleted before the message was saved.");
        }

        return new SendMessageResult(result, conversation with { LastMessage = message }, message);
    }

    private static SendMessageResult Failed(string property, string problem)
    {
        return new SendMessageResult(new ValidationResult([new ValidationFailure(property, problem)]), null, null);
    }

    public async Task<GroupCreationResult> CreateGroupAsync(IReadOnlyList<ContactId> contactIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contactIds);

        List<ContactId> members = [.. contactIds.Distinct()];

        if (members.Count < GroupConversations.MIN_CONTACTS || members.Count > GroupConversations.MAX_CONTACTS)
        {
            return GroupFailed("Contacts", GroupConversations.MIN_CONTACTS == GroupConversations.MAX_CONTACTS
                ? $"A group has {GroupConversations.MAX_CONTACTS} contacts besides you."
                : $"A group has {GroupConversations.MIN_CONTACTS} to {GroupConversations.MAX_CONTACTS} contacts besides you.");
        }

        foreach (ContactId member in members)
        {
            if (await _contacts.GetAsync(member, cancellationToken) is null)
            {
                return GroupFailed("Contacts", "One of the contacts no longer exists.");
            }
        }

        Conversation group = new()
        {
            Id = ConversationId.New(),
            Kind = ConversationKind.Group,
            Participants = [.. members.Select(member => new ConversationParticipant(member))],
            StartedAtUtc = _timeProvider.GetUtcNow(),
            Initiation = GroupInitiation.Occasional
        };

        // When the same contacts already have a group, the store returns that one, rather than starting a second.
        Conversation? stored = await _store.AddGroupAsync(group, cancellationToken);

        return stored is null
            ? GroupFailed("Contacts", "One of the contacts was deleted before the group was saved.")
            : new GroupCreationResult(new ValidationResult(), stored, IsExisting: stored.Id != group.Id);
    }

    private static GroupCreationResult GroupFailed(string property, string problem)
    {
        return new GroupCreationResult(new ValidationResult([new ValidationFailure(property, problem)]), null, IsExisting: false);
    }

    public Task<bool> SetInitiationAsync(ConversationId conversationId, GroupInitiation initiation, CancellationToken cancellationToken = default)
    {
        return _store.SetInitiationAsync(conversationId, initiation, cancellationToken);
    }

    public async Task<GroupTurn?> PlanGroupTurnAsync(ConversationId conversationId, GroupCycle cycle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        if (await _store.GetAsync(conversationId, cancellationToken) is not { IsGroup: true } conversation)
        {
            return null;
        }

        IReadOnlyList<Contact> contacts = await ContactsOfAsync(conversation, cancellationToken);

        if (contacts.Count != conversation.Participants.Count)
        {
            return null;
        }

        IReadOnlyList<ChatMessage> messages = await _store.ListMessagesAsync(conversationId, cancellationToken);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (cycle.Trigger == GroupTrigger.Opened && cycle.TurnsTaken == 0)
        {
            // The chance is used up whatever comes of it, so opening the group again soon brings nothing new. A new group's
            // hello is the exception: it is recorded once written (see ReplyInGroupAsync), so a hello that fails, or that
            // the user interrupts by leaving, is tried again the next time the group opens.
            if (!_coordinator.MayInitiate(conversation, messages, now)
                || (messages.Count > 0 && !await _store.ClaimInitiationAsync(conversationId, now, conversation.LastInitiationAtUtc, cancellationToken)))
            {
                return null;
            }
        }

        IReadOnlyDictionary<ContactId, int>? memoryMatches = messages.LastOrDefault() is { Sender: MessageSender.User } latest
            ? await MemoryMatchesAsync(contacts, latest.Text, cancellationToken)
            : null;
        // Contacts who could not reply this round are passed over, so the others can still answer.
        IReadOnlyList<Contact> available = [.. contacts.Where(contact => !cycle.Unavailable.Contains(contact.Id))];
        GroupDecision decision = _coordinator.Decide(new GroupSituation(conversation, available, messages, cycle, now, memoryMatches));

        if (decision.Speaker is not { } speaker)
        {
            return null;
        }

        if (decision.IsCertain)
        {
            return new GroupTurn(speaker, decision.Reason);
        }

        // Nothing calls for the speaker to write, but they might. Their model decides when that is cheap: asked once a
        // round, and only when it is the model that just wrote, so no other model is loaded just to decide.
        Contact candidate = contacts.First(contact => contact.Id == speaker);

        if (decision.Reason == GroupTurnReason.FollowingUp && !cycle.ModelConsulted && messages.Count > 0
            && await ConsultAsync(conversation, candidate, contacts, messages, cancellationToken) is { } wants)
        {
            return wants ? new GroupTurn(speaker, decision.Reason, ModelConsulted: true) : null;
        }

        return _coordinator.Roll() < decision.Chance ? new GroupTurn(speaker, decision.Reason) : null;
    }

    /// <summary>
    /// For each contact, how many words of <paramref name="text"/> appear in their own memories.
    /// </summary>
    private async Task<IReadOnlyDictionary<ContactId, int>> MemoryMatchesAsync(IReadOnlyList<Contact> contacts, string text, CancellationToken cancellationToken)
    {
        HashSet<string> words = MemoryRecall.Words(text);
        Dictionary<ContactId, int> matches = [];

        foreach (Contact contact in contacts)
        {
            IReadOnlyList<Memory> memories = await _memoryStore.ListAsync(contact.Id, cancellationToken);
            HashSet<string> remembered = [.. memories.SelectMany(memory => MemoryRecall.Words(memory.Text))];
            matches[contact.Id] = words.Count(remembered.Contains);
        }

        return matches;
    }

    /// <summary>
    /// Asks <paramref name="candidate"/>'s model whether they would add a message now: <see langword="true"/> or
    /// <see langword="false"/> for a clear answer, <see langword="null"/> when it was not asked or its answer was unclear,
    /// leaving the decision to chance.
    /// </summary>
    private async Task<bool?> ConsultAsync(Conversation conversation, Contact candidate, IReadOnlyList<Contact> contacts, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        ChatMessage last = messages[^1];
        AgentAssignment? assignment = candidate.Agent;

        if (assignment is null || last.Agent is not { } lastModel
            || !string.Equals(assignment.Provider.Trim(), lastModel.Provider.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(assignment.Model.Trim(), lastModel.Model.Trim(), StringComparison.OrdinalIgnoreCase)
            || FindAgent(assignment) is not { } agent || agent is SimulatedAgent)
        {
            return null;
        }

        UserProfile user = await _profile.GetAsync(cancellationToken);
        AgentRequest request = GroupDecisions.WouldSpeakRequest(conversation, candidate, contacts, user, messages, assignment.Model.Trim());

        try
        {
            AgentReply answer = await agent.ReplyAsync(request, cancellationToken);

            return answer.Status == ReplyStatus.Success ? GroupDecisions.ReadYesNo(answer.Text) : null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Deciding must never stop the group; chance decides instead.
            return null;
        }
    }

    public async Task<ConversationReply> ReplyAsync(ConversationId conversationId, IProgress<ReplyActivity>? activity = null, CancellationToken cancellationToken = default)
    {
        Conversation? conversation = await _store.GetAsync(conversationId, cancellationToken);

        if (conversation is { IsGroup: true })
        {
            return new ConversationReply(ReplyStatus.Unavailable, null, "Who replies in a group is decided turn by turn.");
        }

        Contact? contact = conversation?.ContactId is { } contactId ? await _contacts.GetAsync(contactId, cancellationToken) : null;

        if (conversation is null || contact is null)
        {
            return new ConversationReply(ReplyStatus.NotFound, null, "The conversation no longer exists.");
        }

        return await ReplyAsContactAsync(conversation, contact, [], null, activity, cancellationToken);
    }

    public async Task<ConversationReply> ReplyInGroupAsync(ConversationId conversationId, GroupTurn turn, IProgress<ReplyActivity>? activity = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);

        if (await _store.GetAsync(conversationId, cancellationToken) is not { IsGroup: true } conversation)
        {
            return new ConversationReply(ReplyStatus.NotFound, null, "The conversation no longer exists.");
        }

        IReadOnlyList<Contact> contacts = await ContactsOfAsync(conversation, cancellationToken);
        Contact? speaker = contacts.FirstOrDefault(contact => contact.Id == turn.Speaker);

        if (speaker is null || contacts.Count != conversation.Participants.Count)
        {
            return new ConversationReply(ReplyStatus.NotFound, null, "The contact is no longer in this group.");
        }

        ConversationReply reply = await ReplyAsContactAsync(conversation, speaker, [.. contacts.Where(contact => contact.Id != speaker.Id)], turn.Reason, activity, cancellationToken);

        // A hello, once written, starts the group's cooldown on starting conversations.
        if (turn.Reason == GroupTurnReason.Greeting && reply.Message is { } greeting)
        {
            await _store.ClaimInitiationAsync(conversation.Id, greeting.SentAtUtc, conversation.LastInitiationAtUtc, CancellationToken.None);
        }

        return reply;
    }

    /// <summary>
    /// Asks <paramref name="contact"/>'s own agent, with their own persona and memories, for their next message, and
    /// saves it as theirs. In a group, <paramref name="others"/> are the other contacts and <paramref name="reason"/>
    /// why the contact is writing.
    /// </summary>
    private async Task<ConversationReply> ReplyAsContactAsync(Conversation conversation, Contact contact, IReadOnlyList<Contact> others, GroupTurnReason? reason, IProgress<ReplyActivity>? activity, CancellationToken cancellationToken)
    {
        AgentAssignment assignment = contact.Agent ?? SimulatedAgent.ASSIGNMENT;
        IConversationAgent? agent = FindAgent(assignment);

        if (agent is null)
        {
            string known = string.Join(", ", _agents.Select(candidate => candidate.Provider).Distinct(StringComparer.OrdinalIgnoreCase));
            return new ConversationReply(ReplyStatus.Unavailable, null, $"There is no agent provider named \"{assignment.Provider}\". Available providers: {known}.");
        }

        IReadOnlyList<ChatMessage> messages = await _store.ListMessagesAsync(conversation.Id, cancellationToken);
        UserProfile user = await _profile.GetAsync(cancellationToken);
        DateOnly today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);

        // The agent is given the memories recalled for the latest messages, and the messages after the contact's memory
        // checkpoint. Recall searches this contact's memories only, so no contact is given what another remembers.
        int unremembered = MemoryNotes.FirstUnremembered(messages, conversation.MemoryThroughFor(contact.Id));
        IReadOnlyList<ChatMessage> recent = [.. messages.Skip(unremembered)];
        IReadOnlyList<Memory> recalled = agent is SimulatedAgent
            ? []
            : await _recall.RecallAsync(contact.Id, MemoryRecall.QueryFor(recent, _recallOptions.MaxQueryCharacters), cancellationToken);

        // Canned replies could make no use of what was found, and in a group only a reply to the user's own latest
        // message looks anything up, so contacts answering each other do not search again. Only the latest messages
        // decide, and only a short query made from them is sent; the results inform this reply alone and are kept with
        // it for the user to see.
        bool mayLookUp = agent is not SimulatedAgent && (reason is null || recent.LastOrDefault()?.Sender == MessageSender.User);
        WebLookup? lookup = mayLookUp
            ? await _webSearch.LookUpForReplyAsync(recent, PrivateNames([contact, .. others], user), agent.ReplyAsync, assignment.Model.Trim(), () => activity?.Report(ReplyActivity.SearchingWeb), cancellationToken)
            : null;
        activity?.Report(ReplyActivity.Writing);

        IReadOnlyList<string> remembered = [.. recalled.Select(memory => memory.Text)];
        AgentRequest request = reason is { } why
            ? PersonaFraming.BuildGroup(contact, user, conversation, others, why, remembered, recent, assignment.Model.Trim(), today, lookup?.Sources)
            : PersonaFraming.Build(contact, user, remembered, recent, assignment.Model.Trim(), today, lookup?.Sources);
        AgentReply answer = await agent.ReplyAsync(request, cancellationToken);

        if (answer.Status != ReplyStatus.Success)
        {
            return new ConversationReply(answer.Status, null, answer.Problem);
        }

        string? text = reason is null
            ? PersonaFraming.CleanReply(answer.Text, contact, request)
            : PersonaFraming.CleanGroupReply(answer.Text, contact, user, others, request);

        if (text is null)
        {
            return new ConversationReply(ReplyStatus.InvalidResponse, null, $"{contact.DisplayName}'s agent replied with nothing usable.");
        }

        if (text.Length > ChatMessageValidator.MAX_TEXT_LENGTH)
        {
            text = text[..ChatMessageValidator.MAX_TEXT_LENGTH];
        }

        ChatMessage reply = new()
        {
            Id = MessageId.New(),
            ConversationId = conversation.Id,
            Sender = MessageSender.Contact,
            SenderContactId = contact.Id,
            Text = text,
            SentAtUtc = _timeProvider.GetUtcNow(),
            Agent = assignment,
            WebSearch = lookup
        };

        // A new group's hello is saved only as its first message, so a group opened twice at once is greeted once, and a
        // hello that would follow the user's own first message is dropped.
        if (reason == GroupTurnReason.Greeting)
        {
            return await _store.AddMessageAsync(reply, cancellationToken, onlyIfFirst: true)
                ? new ConversationReply(ReplyStatus.Success, reply, null)
                : new ConversationReply(ReplyStatus.NotFound, null, "The group already has messages, or was deleted.");
        }

        return await _store.AddMessageAsync(reply, cancellationToken)
            ? new ConversationReply(ReplyStatus.Success, reply, null)
            : new ConversationReply(ReplyStatus.NotFound, null, "The conversation was deleted before the reply was saved.");
    }

    public async Task<Conversation?> UpdateMemoryAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        if (await _store.GetAsync(conversationId, cancellationToken) is not { } conversation)
        {
            return null;
        }

        IReadOnlyList<Contact> contacts = await ContactsOfAsync(conversation, cancellationToken);
        Conversation current = conversation;
        bool moved = false;

        // Each contact learns, with their own model and as their own memories, what the user shared where they were
        // present. Canned replies make no use of memories, and have no model to list them.
        foreach (Contact contact in contacts)
        {
            if (contact.Agent is not { } assignment || FindAgent(assignment) is not { } agent || agent is SimulatedAgent)
            {
                continue;
            }

            if (await LearnAsync(current, contact, contacts, assignment, agent, cancellationToken) is { } updated)
            {
                current = updated;
                moved = true;
            }

            // Vectors for what was just learned, and for any memories still waiting: added or corrected by the user, not
            // embedded after an earlier failure, or embedded by a model no longer chosen.
            await _memories.EmbedPendingAsync(contact.Id, cancellationToken);
        }

        return moved ? current : null;
    }

    /// <summary>
    /// Lists the facts in <paramref name="contact"/>'s next batch of messages after their checkpoint, and saves each new
    /// one as their memory as the checkpoint moves past the batch. Returns <see langword="null"/> when there was nothing to
    /// condense or nothing was saved; the batch is then tried again after the next reply.
    /// </summary>
    private async Task<Conversation?> LearnAsync(Conversation conversation, Contact contact, IReadOnlyList<Contact> contacts, AgentAssignment assignment, IConversationAgent agent, CancellationToken cancellationToken)
    {
        IReadOnlyList<ChatMessage> messages = await _store.ListMessagesAsync(conversation.Id, cancellationToken);
        MessageId? checkpoint = conversation.MemoryThroughFor(contact.Id);
        int start = MemoryNotes.FirstUnremembered(messages, checkpoint);

        if (MemoryNotes.FoldBoundary(messages, start) is not { } boundary)
        {
            return null;
        }

        IReadOnlyList<ChatMessage> batch = [.. messages.Take(boundary).Skip(start)];
        UserProfile user = await _profile.GetAsync(cancellationToken);
        IReadOnlyList<Contact> others = conversation.IsGroup ? [.. contacts.Where(other => other.Id != contact.Id)] : [];

        // What is already known, as far as it bears on this batch, so the agent lists only what is new.
        IReadOnlyList<Memory> known = await _recall.RecallAsync(contact.Id, MemoryRecall.QueryForBatch(batch, _recallOptions.MaxQueryCharacters), cancellationToken);
        AgentRequest request = MemoryNotes.ExtractRequest(contact, user, [.. known.Select(memory => memory.Text)], batch, assignment.Model.Trim(), others);
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
        bool saved = await _memoryStore.AddExtractedAsync(conversation.Id, contact.Id, learned, through, checkpoint, cancellationToken);

        return saved ? conversation.WithMemoryThrough(contact.Id, through) : null;
    }

    /// <summary>
    /// The contacts taking part in <paramref name="conversation"/> that still exist, in the order they joined.
    /// </summary>
    private async Task<IReadOnlyList<Contact>> ContactsOfAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        List<Contact> contacts = [];

        foreach (ContactId id in conversation.ContactIds)
        {
            if (await _contacts.GetAsync(id, cancellationToken) is { } contact)
            {
                contacts.Add(contact);
            }
        }

        return contacts;
    }

    /// <summary>
    /// Names that identify the user or the contacts, which are never sent in a search query.
    /// </summary>
    private static IReadOnlyCollection<string> PrivateNames(IEnumerable<Contact> contacts, UserProfile user)
    {
        return [.. contacts.SelectMany(contact => new[] { contact.DisplayName, contact.GivenName, contact.FamilyName, contact.Nickname })
            .Append(user.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];
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
