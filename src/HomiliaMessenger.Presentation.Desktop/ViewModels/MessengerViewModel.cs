using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Base.Extensions;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.Memories;
using HomiliaMessenger.Business.Modules.Speech;
using HomiliaMessenger.Presentation.Desktop.Base.Controls.RibbonControls;
using HomiliaMessenger.Presentation.Desktop.Models;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.ViewModels;

public partial class MessengerViewModel : BaseViewModel, IRibbonProvider
{
    private readonly IConversationService _conversations;
    private readonly IContactService _contacts;
    private readonly IMemoryService _memories;
    private readonly ITextToSpeech? _speech;
    private CancellationTokenSource? _speechSession;

    // Replies are spoken one after another, in the order they are shown, never over each other.
    private Task _speechQueue = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeechToggleHint))]
    private bool _isSpeechOn;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanVoiceReplies))]
    private bool _isSpeechAvailable;

    public bool CanVoiceReplies => IsSpeechAvailable && ActiveContacts().Any(contact => !string.IsNullOrWhiteSpace(contact.VoiceId));
    public string SpeechToggleHint => IsSpeechOn ? "Turn spoken replies off" : "Turn spoken replies on";

    // Conversations whose contact is replying, or whose group is taking turns. Writing again meanwhile restarts the
    // reply so that it answers everything sent so far, once, as a person reading a burst of messages would.
    private readonly Dictionary<ConversationId, CancellationTokenSource> _pendingReplies = [];

    // Which of a group's contacts is writing, while one is.
    private readonly Dictionary<ConversationId, ContactId> _groupSpeakers = [];

    // Conversations whose contact is looking something up online before replying.
    private readonly HashSet<ConversationId> _searching = [];

    // Conversations whose memories are being updated, so updates do not overlap.
    private readonly HashSet<ConversationId> _updatingMemory = [];

    // Unsent composer text per chat, kept while switching between chats: by contact for a direct chat (which may not be
    // saved yet), and by conversation for a group.
    private readonly Dictionary<Guid, string> _drafts = [];

    // Group initiation choices not yet saved, by group, and a gate so saves run one at a time: only the latest choice is
    // written, and an older save finishing late never overrides a newer choice.
    private readonly Dictionary<ConversationId, GroupInitiation> _unsavedInitiation = [];

    // Each group's initiation setting as last saved here, which is what a failed save falls back to showing.
    private readonly Dictionary<ConversationId, GroupInitiation> _savedInitiation = [];

    // Rounds started by opening a group, which turning its initiation off stops.
    private readonly HashSet<ConversationId> _openingRounds = [];
    private readonly SemaphoreSlim _initiationGate = new(1, 1);

    // The message whose avatar carries the continue-conversation button: the open thread's latest from a contact.
    private MessageItem? _continueTarget;

    private bool _syncingListSelection;
    private bool _syncingInitiation;
    private int _threadVersion;

    /// <summary>
    /// The conversation list's selected item. Selecting a conversation opens its thread; clearing the selection (as
    /// filtering does) leaves the open thread alone.
    /// </summary>
    [ObservableProperty] private ConversationListItem? _selectedConversation;

    /// <summary>
    /// Who the open direct chat is with, or <see langword="null"/> when no direct chat is open.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveThread), nameof(ActiveSubtitle), nameof(CanVoiceReplies), nameof(ActiveTitle), nameof(ActivePhoto), nameof(CanInvite), nameof(ComposerPlaceholder), nameof(EmptyThreadHint), nameof(TypingContact))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(InviteContactCommand))]
    private Contact? _activeContact;

    /// <summary>
    /// The open group, or <see langword="null"/> when no group is open.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveThread), nameof(IsGroupThread), nameof(ActiveSubtitle), nameof(CanVoiceReplies), nameof(ActiveTitle), nameof(ActivePhoto), nameof(ActiveSecondPhoto), nameof(CanInvite), nameof(ComposerPlaceholder), nameof(EmptyThreadHint), nameof(TypingContact))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(InviteContactCommand))]
    private Conversation? _activeGroup;

    /// <summary>
    /// The open thread's conversation, or <see langword="null"/> for a new direct chat that has no messages yet.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConversation))]
    [NotifyCanExecuteChangedFor(nameof(EditMemoryCommand))]
    private ConversationId? _activeConversationId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draftText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveSubtitle), nameof(TypingContact))]
    private bool _isContactTyping;

    /// <summary>
    /// Whether the open chat's reply, or its group's round, can be stopped: while it is under way, including while a
    /// group decides who writes next.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopRepliesCommand))]
    private bool _canStopReplies;

    /// <summary>
    /// Whether the open chat's contact is looking something up online before replying.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveSubtitle))]
    private bool _isContactSearching;

    /// <summary>
    /// Whether the open group's contacts may start conversations when it is opened.
    /// </summary>
    [ObservableProperty] private bool _isGroupInitiationOn;

    [ObservableProperty] private bool _hasMessages;

    /// <summary>
    /// Why the open chat's contact could not reply, shown in the thread until the next message is sent.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReplyProblem))]
    private string? _replyProblem;
    [ObservableProperty] private bool _hasConversations;
    [ObservableProperty] private bool _hasContacts;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _contactSearchText = string.Empty;
    [ObservableProperty] private string _status = "Loading chats…";

    public ObservableCollection<ConversationListItem> Conversations { get; } = [];
    public ObservableCollection<ConversationListItem> VisibleConversations { get; } = [];
    public ObservableCollection<MessageItem> Messages { get; } = [];
    public ObservableCollection<Contact> Contacts { get; } = [];

    /// <summary>
    /// Contacts offered when starting a new chat, filtered by <see cref="ContactSearchText"/>.
    /// </summary>
    public ObservableCollection<Contact> VisibleContacts { get; } = [];

    /// <summary>
    /// The open group's contacts, in the order they joined.
    /// </summary>
    public ObservableCollection<Contact> GroupContacts { get; } = [];

    /// <summary>
    /// Contacts who can be invited to make a group with the open direct chat's contact.
    /// </summary>
    public ObservableCollection<Contact> InviteCandidates { get; } = [];

    /// <summary>
    /// Supplied by the view to ask the user to confirm an action, given a title and a message.
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// Supplied by the view to let the user read and correct what a contact remembers, until the user is done.
    /// </summary>
    public Func<MemoryEditorViewModel, Task>? ShowMemoryEditorAsync { get; set; }

    public Type RibbonControlType => typeof(MessengerRibbonControl);

    public bool HasActiveThread => ActiveContact is not null || ActiveGroup is not null;

    /// <summary>
    /// Whether the open chat's reply, or its group's round of turns, is still under way, including while a group decides
    /// who writes next.
    /// </summary>
    internal bool IsReplyPending => ActiveConversationId is { } id && _pendingReplies.ContainsKey(id);

    public bool IsGroupThread => ActiveGroup is not null;

    /// <summary>
    /// Whether the open chat's contacts can be asked to keep talking without a new message: a contact has written in it,
    /// and no reply or round is under way.
    /// </summary>
    public bool CanContinue => _continueTarget is not null && ActiveConversationId is { } id && !_pendingReplies.ContainsKey(id);

    public bool HasReplyProblem => ReplyProblem is not null;

    /// <summary>
    /// Whether the open direct chat has a saved conversation, and so memories to show.
    /// </summary>
    public bool HasConversation => ActiveConversationId is not null && ActiveContact is not null;

    /// <summary>
    /// Whether another contact can be invited: from a direct chat, which starts a new group and leaves the chat as it
    /// is. A group already has as many contacts as a group may (<see cref="GroupConversations.MAX_CONTACTS"/>).
    /// </summary>
    public bool CanInvite => ActiveContact is not null && GroupConversations.MAX_CONTACTS > 1;

    public string ActiveTitle => ActiveGroup is { } group ? GroupConversations.DisplayName(group, GroupContacts) : ActiveContact?.DisplayName ?? string.Empty;

    public byte[]? ActivePhoto => ActiveContact?.Photo ?? GroupContacts.FirstOrDefault()?.Photo;

    public byte[]? ActiveSecondPhoto => IsGroupThread ? GroupContacts.Skip(1).FirstOrDefault()?.Photo : null;

    public string ComposerPlaceholder => IsGroupThread ? "Message the group" : $"Message {ActiveContact?.DisplayName}";

    public string EmptyThreadHint => IsGroupThread ? "Say hello to the group." : $"Say hello to {ActiveContact?.DisplayName}.";

    /// <summary>
    /// The contact shown typing: the open chat's contact, or whichever of the group's contacts is writing.
    /// </summary>
    public Contact? TypingContact => ActiveGroup is { } group
        ? _groupSpeakers.TryGetValue(group.Id, out ContactId speaker) ? GroupContacts.FirstOrDefault(contact => contact.Id == speaker) : null
        : ActiveContact;

    public string ActiveSubtitle
    {
        get
        {
            if (IsContactTyping)
            {
                string activity = IsContactSearching ? "looking something up…" : "typing…";
                return IsGroupThread && TypingContact is { } typing ? $"{typing.DisplayName} is {activity}" : activity;
            }

            if (IsGroupThread)
            {
                return "You, " + string.Join(", ", GroupContacts.Select(contact => contact.DisplayName));
            }

            return ActiveContact?.Agent is { } agent ? $"{agent.Provider} · {agent.Model}" : "Simulated replies";
        }
    }

    public MessengerViewModel(IConversationService conversations, IContactService contacts, IMemoryService memories, ITextToSpeech? speech = null)
    {
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        _memories = memories ?? throw new ArgumentNullException(nameof(memories));
        _speech = speech;
        Messages.CollectionChanged += (_, _) => UpdateContinueTarget();
    }

    /// <summary>
    /// Moves the continue-conversation button to the latest message from a contact, where it shows once per thread.
    /// </summary>
    private void UpdateContinueTarget()
    {
        MessageItem? target = Messages.LastOrDefault(item => !item.IsFromUser);

        if (target is not null && !target.CanCarryContinue)
        {
            target = null;
        }

        if (!ReferenceEquals(target, _continueTarget))
        {
            if (_continueTarget is not null)
            {
                _continueTarget.ShowsContinue = false;
            }

            if (target is not null)
            {
                target.ShowsContinue = true;
            }

            _continueTarget = target;
        }

        NotifyCanContinue();
    }

    private void NotifyCanContinue()
    {
        OnPropertyChanged(nameof(CanContinue));
        ContinueCommand.NotifyCanExecuteChanged();
        ContinueWithCommand.NotifyCanExecuteChanged();
        ContinueGroupCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Asks the open direct chat's contact to keep the conversation going, without the user writing anything.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void Continue()
    {
        if (ActiveGroup is not null || ActiveContact is null || ActiveConversationId is not { } conversationId || !CanContinue)
        {
            return;
        }

        ReplyProblem = null;
        StartReply(conversationId, continuing: true);
    }

    /// <summary>
    /// Asks one of the open group's contacts to write next, once, without the user writing anything.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void ContinueWith(Contact? contact)
    {
        if (ActiveGroup is not { } group || contact is null || !GroupContacts.Any(member => member.Id == contact.Id) || !CanContinue)
        {
            return;
        }

        ReplyProblem = null;
        StartGroupRound(group.Id, GroupTrigger.Continued, new GroupTurn(contact.Id, GroupTurnReason.Continuing));
    }

    /// <summary>
    /// Asks the open group to keep talking: the group decides who writes next, and the others may join in, within a
    /// round's usual limits. This works whether or not the group may start conversations on its own.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void ContinueGroup()
    {
        if (ActiveGroup is not { } group || !CanContinue)
        {
            return;
        }

        ReplyProblem = null;
        StartGroupRound(group.Id, GroupTrigger.Continued);
    }

    partial void OnActiveContactChanged(Contact? oldValue, Contact? newValue)
    {
        if (oldValue?.Id != newValue?.Id || oldValue?.VoiceId != newValue?.VoiceId)
        {
            StopSpeech();
        }
    }

    partial void OnActiveGroupChanged(Conversation? oldValue, Conversation? newValue)
    {
        if (oldValue?.Id != newValue?.Id)
        {
            StopSpeech();
        }

        _syncingInitiation = true;

        try
        {
            IsGroupInitiationOn = newValue?.Initiation == GroupInitiation.Occasional;
        }
        finally
        {
            _syncingInitiation = false;
        }
    }

    partial void OnIsGroupInitiationOnChanged(bool value)
    {
        if (!_syncingInitiation && ActiveGroup is { } group)
        {
            _unsavedInitiation[group.Id] = value ? GroupInitiation.Occasional : GroupInitiation.Off;

            // A contact starting a conversation right now, unprompted, stops too.
            if (!value && _openingRounds.Contains(group.Id))
            {
                CancelReply(group.Id);
                UpdateTyping();
            }

            _ = SaveInitiationAsync(group.Id);
        }
    }

    private async Task SaveInitiationAsync(ConversationId groupId)
    {
        await _initiationGate.WaitAsync();

        try
        {
            // A choice already saved by an earlier call, which took the latest, needs no second write.
            if (!_unsavedInitiation.Remove(groupId, out GroupInitiation initiation))
            {
                return;
            }

            if (!await _conversations.SetInitiationAsync(groupId, initiation))
            {
                Status = "Could not save the group setting: the group no longer exists.";
                ShowSavedInitiation(groupId);
                return;
            }

            _savedInitiation[groupId] = initiation;

            if (_unsavedInitiation.ContainsKey(groupId))
            {
                // Saved, but the user has changed it again since: that newer choice is saved and shown next.
                return;
            }

            if (ActiveGroup?.Id == groupId)
            {
                ActiveGroup = ActiveGroup with { Initiation = initiation };
            }

            if (Conversations.FirstOrDefault(item => item.Id == groupId) is { } item)
            {
                item.Conversation = item.Conversation with { Initiation = initiation };
            }

            Status = initiation == GroupInitiation.Off
                ? "The group will wait for you to write first."
                : "The group may now and then start a conversation when you open it.";
        }
        catch (Exception error)
        {
            Status = "Could not save the group setting: " + error.Message;
            ShowSavedInitiation(groupId);
        }
        finally
        {
            _initiationGate.Release();
        }
    }

    /// <summary>
    /// After a choice could not be saved, shows the group's setting as it is saved, unless the user has made another
    /// choice since, which is still to be saved.
    /// </summary>
    private void ShowSavedInitiation(ConversationId groupId)
    {
        if (ActiveGroup?.Id != groupId || _unsavedInitiation.ContainsKey(groupId))
        {
            return;
        }

        GroupInitiation saved = _savedInitiation.TryGetValue(groupId, out GroupInitiation latest) ? latest : ActiveGroup.Initiation;
        ActiveGroup = ActiveGroup with { Initiation = saved };

        _syncingInitiation = true;

        try
        {
            IsGroupInitiationOn = saved == GroupInitiation.Occasional;
        }
        finally
        {
            _syncingInitiation = false;
        }
    }

    partial void OnIsSpeechOnChanged(bool value)
    {
        _speechSession?.Cancel();
        _speechSession?.Dispose();
        _speechSession = value ? new CancellationTokenSource() : null;
    }

    public void StopSpeech()
    {
        IsSpeechOn = false;
    }

    /// <summary>
    /// Called when the messenger is no longer on screen: stops speech, and any group's turn-taking, since a group's
    /// contacts only talk while the user has it open.
    /// </summary>
    public void Suspend()
    {
        StopSpeech();

        if (ActiveGroup is { } group)
        {
            CancelReply(group.Id);
            UpdateTyping();
        }
    }

    [RelayCommand]
    private void ToggleSpeech()
    {
        IsSpeechAvailable = _speech?.Model.IsAvailable == true;
        if (CanVoiceReplies)
        {
            IsSpeechOn = !IsSpeechOn;
        }
        else
        {
            StopSpeech();
        }
    }

    /// <summary>
    /// Speaks <paramref name="reply"/> in its sender's own voice, after any reply still being spoken. Contacts without a
    /// voice are skipped, and the conversation never waits for speech.
    /// </summary>
    private void Voice(ChatMessage reply)
    {
        if (!IsSpeechOn || _speech is null || _speechSession is not { } session)
        {
            return;
        }

        ContactId? senderId = ActiveGroup?.SenderOf(reply) ?? ActiveContact?.Id;
        Contact? sender = ActiveContacts().FirstOrDefault(contact => contact.Id == senderId);

        if (sender?.VoiceId is not { Length: > 0 } voiceId)
        {
            return;
        }

        Task previous = _speechQueue;
        _speechQueue = SpeakInTurnAsync(previous, reply.Text, voiceId, session);
    }

    private async Task SpeakInTurnAsync(Task previous, string text, string voiceId, CancellationTokenSource session)
    {
        await previous;

        if (!session.IsCancellationRequested)
        {
            await VoiceReplyAsync(text, voiceId, session);
        }
    }

    private async Task VoiceReplyAsync(string text, string voiceId, CancellationTokenSource session)
    {
        try
        {
            SpeechResult result = await _speech!.SpeakAsync(text, voiceId, session.Token);
            if (!result.IsSuccessful && ReferenceEquals(session, _speechSession))
            {
                Status = result.Problem ?? "Could not voice the reply.";
                StopSpeech();
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            // Speech was switched off, the chat changed, or its view closed.
        }
        catch (ObjectDisposedException)
        {
            // Speech was switched off while this reply waited its turn.
        }
        catch (Exception error)
        {
            if (ReferenceEquals(session, _speechSession))
            {
                Status = "Could not voice the reply: " + error.Message;
                StopSpeech();
            }
        }
    }

    /// <summary>
    /// The open chat's contacts: the direct chat's contact, or the group's.
    /// </summary>
    private IEnumerable<Contact> ActiveContacts()
    {
        return ActiveContact is { } contact ? [contact] : GroupContacts;
    }

    /// <summary>
    /// Reloads on every activation, since contacts may have been edited or deleted on the Contacts page meanwhile.
    /// </summary>
    public override Task InitializeAsync()
    {
        return TryRefreshAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => TryRefreshAsync();

    private async Task<bool> TryRefreshAsync()
    {
        IsSpeechAvailable = _speech?.Model.IsAvailable == true;
        if (!IsSpeechAvailable)
        {
            StopSpeech();
        }

        try
        {
            IsBusy = true;

            IReadOnlyList<Contact> contacts = await _contacts.ListAsync();
            IReadOnlyList<Conversation> conversations = await _conversations.ListAsync();
            Dictionary<ContactId, Contact> contactsById = contacts.ToDictionary(contact => contact.Id);

            Contacts.Clear();

            foreach (Contact contact in contacts)
            {
                Contacts.Add(contact);
            }

            HasContacts = Contacts.Count > 0;
            FilterContacts();

            Conversations.Clear();

            foreach (Conversation conversation in conversations)
            {
                if (ContactsOf(conversation, contactsById) is { } members)
                {
                    Conversations.Add(new ConversationListItem(conversation, members) { IsTyping = IsTyping(conversation.Id) });
                }
            }

            HasConversations = Conversations.Count > 0;
            FilterConversations();

            await RefreshActiveThreadAsync(contactsById, conversations);

            Status = CountStatus();
            return true;
        }
        catch (Exception error)
        {
            Status = "Could not load chats: " + error.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// A conversation's contacts, or <see langword="null"/> when any of them no longer exists.
    /// </summary>
    private static IReadOnlyList<Contact>? ContactsOf(Conversation conversation, Dictionary<ContactId, Contact> contactsById)
    {
        List<Contact> members = [];

        foreach (ContactId id in conversation.ContactIds)
        {
            if (!contactsById.TryGetValue(id, out Contact? contact))
            {
                return null;
            }

            members.Add(contact);
        }

        return members.Count == 0 ? null : members;
    }

    /// <summary>
    /// Brings the open thread up to date after a reload: closes it if its contact (or a group's contact) was deleted, and
    /// otherwise reloads it so edits to the contacts (such as a new photo) show.
    /// </summary>
    private async Task RefreshActiveThreadAsync(Dictionary<ContactId, Contact> contactsById, IReadOnlyList<Conversation> conversations)
    {
        if (ActiveGroup is { } group)
        {
            Conversation? stored = conversations.FirstOrDefault(item => item.Id == group.Id);

            if (stored is null || ContactsOf(stored, contactsById) is not { } members)
            {
                CloseThread();
                return;
            }

            await OpenGroupAsync(stored, members, reloadSameThread: true);
            return;
        }

        if (ActiveContact is null)
        {
            return;
        }

        if (!contactsById.TryGetValue(ActiveContact.Id, out Contact? contact))
        {
            CloseThread();
            return;
        }

        Conversation? conversation = conversations.FirstOrDefault(item => item.ContactId == contact.Id);
        await OpenThreadAsync(contact, conversation?.Id, reloadSameThread: true);
    }

    partial void OnSearchTextChanged(string value)
    {
        FilterConversations();
    }

    partial void OnContactSearchTextChanged(string value)
    {
        FilterContacts();
    }

    private void FilterConversations()
    {
        VisibleConversations.Clear();

        foreach (ConversationListItem item in Conversations)
        {
            if (MatchesSearch(item))
            {
                VisibleConversations.Add(item);
            }
        }

        SyncListSelection();
    }

    private bool MatchesSearch(ConversationListItem item)
    {
        string query = SearchText.Trim();

        return query.Length == 0
            || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Contacts.Any(contact => contact.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            || item.Conversation.LastMessage?.Text.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void FilterContacts()
    {
        string query = ContactSearchText.Trim();
        VisibleContacts.Clear();

        foreach (Contact contact in Contacts)
        {
            if (query.Length == 0 || contact.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                VisibleContacts.Add(contact);
            }
        }

        FillInviteCandidates();
    }

    private void FillInviteCandidates()
    {
        InviteCandidates.Clear();

        if (ActiveContact is not { } current)
        {
            return;
        }

        foreach (Contact contact in Contacts.Where(contact => contact.Id != current.Id))
        {
            InviteCandidates.Add(contact);
        }
    }

    private void SyncListSelection()
    {
        _syncingListSelection = true;

        try
        {
            SelectedConversation = VisibleConversations.FirstOrDefault(item => item.Id == ActiveConversationId);
        }
        finally
        {
            _syncingListSelection = false;
        }
    }

    partial void OnSelectedConversationChanged(ConversationListItem? value)
    {
        if (_syncingListSelection || value is null || value.Id == ActiveConversationId)
        {
            return;
        }

        _ = value.IsGroup ? OpenGroupAsync(value.Conversation, value.Contacts) : OpenThreadAsync(value.Contact, value.Id);
    }

    /// <summary>
    /// Opens the chat with <paramref name="contact"/>: their existing conversation if they have one, otherwise an empty
    /// thread whose conversation starts with the first message sent.
    /// </summary>
    [RelayCommand]
    private async Task StartChatAsync(Contact? contact)
    {
        if (contact is null)
        {
            return;
        }

        ContactSearchText = string.Empty;

        ConversationListItem? listed = Conversations.FirstOrDefault(item => !item.IsGroup && item.Contact.Id == contact.Id);

        if (listed is not null)
        {
            // Clear any filter that would hide the chat, so it shows as selected.
            SearchText = string.Empty;
            await OpenThreadAsync(listed.Contact, listed.Id);
            return;
        }

        try
        {
            Conversation? stored = await _conversations.FindByContactAsync(contact.Id);
            await OpenThreadAsync(contact, stored?.Id);
        }
        catch (Exception error)
        {
            Status = "Could not open the chat: " + error.Message;
        }
    }

    /// <summary>
    /// Starts a group with the open direct chat's contact and <paramref name="invited"/>, and opens it. The direct chat,
    /// and what its contact remembers from it, is left as it is. If the two already have a group, that group opens.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInvite))]
    private async Task InviteContactAsync(Contact? invited)
    {
        if (ActiveContact is not { } current || invited is null || invited.Id == current.Id)
        {
            return;
        }

        GroupCreationResult result;

        try
        {
            result = await _conversations.CreateGroupAsync([current.Id, invited.Id]);
        }
        catch (Exception error)
        {
            Status = "Could not start the group: " + error.Message;
            return;
        }

        if (!result.Validation.IsValid || result.Group is not { } group)
        {
            Status = result.Validation.ToStringPretty("Group");
            return;
        }

        IReadOnlyList<Contact> members = [current, invited];
        ConversationListItem? item = Conversations.FirstOrDefault(existing => existing.Id == group.Id);

        if (item is null)
        {
            item = new ConversationListItem(group, members);
            Conversations.Insert(0, item);
            HasConversations = true;
            Status = CountStatus();
        }

        SearchText = string.Empty;
        FilterConversations();
        await OpenGroupAsync(item.Conversation, item.Contacts);
    }

    private string? DraftFor(ConversationId? conversationId)
    {
        return ActiveContact is { } contact ? _drafts.GetValueOrDefault(contact.Id.Value) : conversationId is { } id ? _drafts.GetValueOrDefault(id.Value) : null;
    }

    /// <summary>
    /// Remembers the open chat's unsent text, and leaves it: a group stops taking turns once the user leaves it.
    /// </summary>
    private void LeaveActiveThread()
    {
        if (ActiveContact is { } contact)
        {
            _drafts[contact.Id.Value] = DraftText;
        }
        else if (ActiveGroup is { } group)
        {
            _drafts[group.Id.Value] = DraftText;
            CancelReply(group.Id);
        }
    }

    private async Task OpenThreadAsync(Contact contact, ConversationId? conversationId, bool reloadSameThread = false)
    {
        bool sameThread = ActiveContact?.Id == contact.Id;

        if (!sameThread)
        {
            LeaveActiveThread();
            DraftText = _drafts.GetValueOrDefault(contact.Id.Value, string.Empty);
        }
        else if (!reloadSameThread && ActiveConversationId == conversationId)
        {
            return;
        }

        int version = ++_threadVersion;

        if (!sameThread)
        {
            ReplyProblem = null;
        }

        ActiveGroup = null;
        GroupContacts.Clear();
        ActiveContact = contact;
        ActiveConversationId = conversationId;
        FillInviteCandidates();
        UpdateTyping();
        SyncListSelection();

        if (!sameThread || conversationId is null)
        {
            Messages.Clear();
            HasMessages = false;
        }

        if (conversationId is null)
        {
            return;
        }

        await LoadMessagesAsync(conversationId.Value, version, null);
    }

    /// <summary>
    /// Opens <paramref name="group"/>. Opening it (or coming back to it) may let one of its contacts start a conversation,
    /// now and then: the group decides, within its cooldown.
    /// </summary>
    private async Task OpenGroupAsync(Conversation group, IReadOnlyList<Contact> members, bool reloadSameThread = false)
    {
        bool sameThread = ActiveGroup?.Id == group.Id;

        if (sameThread && !reloadSameThread)
        {
            return;
        }

        if (!sameThread)
        {
            LeaveActiveThread();
            DraftText = _drafts.GetValueOrDefault(group.Id.Value, string.Empty);
            ReplyProblem = null;
        }

        int version = ++_threadVersion;

        ActiveContact = null;
        GroupContacts.Clear();

        foreach (Contact member in members)
        {
            GroupContacts.Add(member);
        }

        ActiveGroup = group;
        ActiveConversationId = group.Id;
        InviteCandidates.Clear();
        OnPropertyChanged(nameof(ActiveTitle));
        OnPropertyChanged(nameof(ActivePhoto));
        OnPropertyChanged(nameof(ActiveSecondPhoto));
        OnPropertyChanged(nameof(ActiveSubtitle));
        OnPropertyChanged(nameof(CanVoiceReplies));
        UpdateTyping();
        SyncListSelection();

        if (!sameThread)
        {
            Messages.Clear();
            HasMessages = false;
        }

        if (await LoadMessagesAsync(group.Id, version, group) && !_pendingReplies.ContainsKey(group.Id))
        {
            StartGroupRound(group.Id, GroupTrigger.Opened);
        }
    }

    /// <returns>Whether the messages loaded and the thread is still open.</returns>
    private async Task<bool> LoadMessagesAsync(ConversationId conversationId, int version, Conversation? group)
    {
        try
        {
            IReadOnlyList<ChatMessage> messages = await _conversations.ListMessagesAsync(conversationId);

            // The user may have switched chats while the messages loaded.
            if (version != _threadVersion)
            {
                return false;
            }

            // Messages shown while these loaded (one the user sent, or a reply) may be newer than what was read; they
            // are kept, after the loaded ones, in the order they were sent.
            HashSet<MessageId> loaded = [.. messages.Select(message => message.Id)];
            List<ChatMessage> shownSince = [.. Messages.Select(item => item.Message).Where(message => message.ConversationId == conversationId && !loaded.Contains(message.Id))];

            Messages.Clear();

            foreach (ChatMessage message in messages.Concat(shownSince).OrderBy(message => message.SentAtUtc))
            {
                AppendMessage(message, group);
            }

            HasMessages = Messages.Count > 0;
            return true;
        }
        catch (Exception error)
        {
            Status = "Could not load messages: " + error.Message;
            return false;
        }
    }

    private void CloseThread()
    {
        _threadVersion++;
        LeaveActiveThread();

        ActiveContact = null;
        ActiveGroup = null;
        GroupContacts.Clear();
        InviteCandidates.Clear();
        ActiveConversationId = null;
        ReplyProblem = null;
        DraftText = string.Empty;
        Messages.Clear();
        HasMessages = false;
        UpdateTyping();
        SyncListSelection();
    }

    private void AppendMessage(ChatMessage message, Conversation? group = null)
    {
        // A reply saved just before a reload is already in the reloaded thread.
        if (Messages.Any(item => item.Id == message.Id))
        {
            return;
        }

        group ??= ActiveGroup;
        Contact? sender = group is not null
            ? GroupContacts.FirstOrDefault(contact => contact.Id == group.SenderOf(message))
            : ActiveContact;

        Messages.Add(MessageItem.Follow(Messages.LastOrDefault(), message, DateTimeOffset.Now, sender, namesSender: group is not null));
        HasMessages = true;
    }

    private bool CanSend()
    {
        return HasActiveThread && !string.IsNullOrWhiteSpace(DraftText);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (ActiveGroup is { } group)
        {
            await SendToGroupAsync(group);
            return;
        }

        if (ActiveContact is not { } contact || string.IsNullOrWhiteSpace(DraftText))
        {
            return;
        }

        string text = DraftText.Trim();

        // Clear the composer right away, like a messenger does, and restore the text if sending fails.
        DraftText = string.Empty;
        ReplyProblem = null;
        SendMessageResult result;

        try
        {
            result = await _conversations.SendAsync(contact.Id, text);
        }
        catch (Exception error)
        {
            Status = "Could not send: " + error.Message;
            RestoreDraft(contact.Id.Value, text);
            return;
        }

        if (!result.Validation.IsValid || result.Conversation is null || result.Message is null)
        {
            Status = result.Validation.ToStringPretty("Send");
            RestoreDraft(contact.Id.Value, text);
            return;
        }

        if (ActiveContact?.Id == contact.Id)
        {
            ActiveConversationId = result.Conversation.Id;
            AppendMessage(result.Message);
        }

        UpsertListItem(result.Conversation, [contact]);
        StartReply(result.Conversation.Id);
    }

    private async Task SendToGroupAsync(Conversation group)
    {
        if (string.IsNullOrWhiteSpace(DraftText))
        {
            return;
        }

        string text = DraftText.Trim();
        DraftText = string.Empty;
        ReplyProblem = null;

        // Whatever the group was saying is set aside: the contacts now answer this, as a new round.
        CancelReply(group.Id);
        UpdateTyping();

        SendMessageResult result;

        try
        {
            result = await _conversations.SendAsync(group.Id, text);
        }
        catch (Exception error)
        {
            Status = "Could not send: " + error.Message;
            RestoreDraft(group.Id.Value, text);
            return;
        }

        if (!result.Validation.IsValid || result.Conversation is null || result.Message is null)
        {
            Status = result.Validation.ToStringPretty("Send");
            RestoreDraft(group.Id.Value, text);
            return;
        }

        if (ActiveGroup?.Id == group.Id)
        {
            AppendMessage(result.Message);
        }

        UpsertListItem(result.Conversation, [.. GroupContacts]);

        if (ActiveGroup?.Id == group.Id)
        {
            StartGroupRound(group.Id, GroupTrigger.UserMessage);
        }
    }

    private void RestoreDraft(Guid key, string text)
    {
        bool open = ActiveContact?.Id.Value == key || ActiveGroup?.Id.Value == key;

        if (open)
        {
            if (string.IsNullOrEmpty(DraftText))
            {
                DraftText = text;
            }
        }
        else if (string.IsNullOrEmpty(_drafts.GetValueOrDefault(key)))
        {
            _drafts[key] = text;
        }
    }

    private void UpsertListItem(Conversation conversation, IReadOnlyList<Contact> members)
    {
        ConversationListItem? item = Conversations.FirstOrDefault(existing => existing.Id == conversation.Id);

        if (item is null)
        {
            item = new ConversationListItem(conversation, members);
            Conversations.Insert(0, item);
            HasConversations = true;

            if (MatchesSearch(item))
            {
                VisibleConversations.Insert(0, item);
            }

            Status = CountStatus();
        }
        else
        {
            item.Conversation = conversation;
            MoveToTop(item);
        }

        SyncListSelection();
    }

    private void MoveToTop(ConversationListItem item)
    {
        int index = Conversations.IndexOf(item);

        if (index > 0)
        {
            Conversations.Move(index, 0);
        }

        // A search can match the latest message, so a new message may change which chats match.
        if (SearchText.Trim().Length > 0)
        {
            FilterConversations();
            return;
        }

        int visibleIndex = VisibleConversations.IndexOf(item);

        if (visibleIndex > 0)
        {
            _syncingListSelection = true;

            try
            {
                VisibleConversations.Move(visibleIndex, 0);
            }
            finally
            {
                _syncingListSelection = false;
            }

            // Moving the selected row can clear the list's selection.
            SyncListSelection();
        }
    }

    /// <param name="continuing">Whether the contact keeps the conversation going rather than answering the user.</param>
    private void StartReply(ConversationId conversationId, bool continuing = false)
    {
        CancelReply(conversationId);

        CancellationTokenSource pending = new();
        _pendingReplies[conversationId] = pending;
        UpdateTyping();
        _ = ReplyAsync(conversationId, pending, continuing);
    }

    private void CancelReply(ConversationId conversationId)
    {
        if (_pendingReplies.Remove(conversationId, out CancellationTokenSource? superseded))
        {
            superseded.Cancel();
        }

        _searching.Remove(conversationId);
        _groupSpeakers.Remove(conversationId);
        _openingRounds.Remove(conversationId);
    }

    /// <summary>
    /// Stops the open chat's contact replying, or its group taking turns, until the user writes again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStopReplies))]
    private void StopReplies()
    {
        if (ActiveConversationId is { } conversationId)
        {
            CancelReply(conversationId);
            UpdateTyping();
            Status = "Stopped.";
        }
    }

    private async Task ReplyAsync(ConversationId conversationId, CancellationTokenSource pending, bool continuing)
    {
        ChatMessage? reply = null;
        string? problem = null;

        try
        {
            // Reported on this (the UI) thread, since Progress captures it.
            Progress<ReplyActivity> activity = new(stage => ShowActivity(conversationId, pending, stage));
            ConversationReply result = continuing
                ? await _conversations.ContinueAsync(conversationId, activity, pending.Token)
                : await _conversations.ReplyAsync(conversationId, activity, pending.Token);
            reply = result.Message;

            // A deleted conversation needs no explanation.
            if (result.Status is not ReplyStatus.Success and not ReplyStatus.NotFound)
            {
                problem = result.Problem ?? "The contact could not reply.";
            }
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
            // Superseded by a newer reply, or the chat was deleted.
        }
        catch (Exception error)
        {
            problem = "The contact could not reply: " + error.Message;
        }

        bool current = FinishPending(conversationId, pending);

        if (problem is not null && current)
        {
            ShowProblem(conversationId, problem);
        }

        // A reply that finished just before being superseded was already saved, so it is still shown.
        if (reply is not null)
        {
            ShowReply(conversationId, reply);
            _ = UpdateMemoryAsync(conversationId);
        }

        UpdateTyping();
    }

    /// <summary>
    /// Starts a round of the group taking turns: after the user writes, when the user opens the group, or when the user
    /// asks it to continue. Each turn, the group decides who writes next, if anyone; the round ends when it decides to
    /// wait for the user, after at most a few messages, or as soon as the user writes again, stops it, or leaves the group.
    /// </summary>
    /// <param name="only">The one turn the round consists of, when the user chose who writes.</param>
    private void StartGroupRound(ConversationId conversationId, GroupTrigger trigger, GroupTurn? only = null)
    {
        CancelReply(conversationId);

        CancellationTokenSource pending = new();
        _pendingReplies[conversationId] = pending;

        if (trigger == GroupTrigger.Opened)
        {
            _openingRounds.Add(conversationId);
        }

        UpdateTyping();
        _ = RunGroupRoundAsync(conversationId, trigger, pending, only);
    }

    private async Task RunGroupRoundAsync(ConversationId conversationId, GroupTrigger trigger, CancellationTokenSource pending, GroupTurn? only)
    {
        GroupCycle cycle = new(trigger);
        bool replied = false;
        string? problem = null;

        try
        {
            while (!pending.IsCancellationRequested && ActiveGroup?.Id == conversationId)
            {
                GroupTurn? turn = only is null
                    ? await _conversations.PlanGroupTurnAsync(conversationId, cycle, pending.Token)
                    : cycle.TurnsTaken == 0 ? only : null;

                // Only an open group talks; one left meanwhile has been canceled.
                if (turn is null || pending.IsCancellationRequested || ActiveGroup?.Id != conversationId)
                {
                    break;
                }

                _groupSpeakers[conversationId] = turn.Speaker;
                UpdateTyping();

                Progress<ReplyActivity> activity = new(stage => ShowActivity(conversationId, pending, stage));
                ConversationReply result = await _conversations.ReplyInGroupAsync(conversationId, turn, activity, pending.Token);

                if (result.Message is { } reply)
                {
                    replied = true;
                    ShowReply(conversationId, reply);
                }

                if (result.Status == ReplyStatus.NotFound)
                {
                    // The group was deleted.
                    break;
                }

                _groupSpeakers.Remove(conversationId);
                _searching.Remove(conversationId);

                if (result.Status != ReplyStatus.Success)
                {
                    // Shown now, while the others may still answer; this contact is not asked again this round.
                    string name = GroupContacts.FirstOrDefault(contact => contact.Id == turn.Speaker)?.DisplayName ?? "A contact";
                    problem = result.Problem ?? $"{name} could not reply.";
                    ShowProblem(conversationId, problem);
                    UpdateTyping();
                    cycle = cycle.AfterFailure(turn);
                    continue;
                }

                cycle = cycle.After(turn);
            }
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
            // The user wrote again, stopped the group, or left it.
        }
        catch (Exception error)
        {
            problem = "The group could not reply: " + error.Message;
        }

        bool current = FinishPending(conversationId, pending);

        if (problem is not null && current)
        {
            ShowProblem(conversationId, problem);
        }
        else if (current && trigger == GroupTrigger.Continued && cycle.TurnsTaken == 0)
        {
            Status = "No one in the group had anything to add.";
        }

        if (replied)
        {
            _ = UpdateMemoryAsync(conversationId);
        }

        UpdateTyping();
    }

    /// <summary>
    /// Ends a reply or round that has finished, returning whether it was still the current one.
    /// </summary>
    private bool FinishPending(ConversationId conversationId, CancellationTokenSource pending)
    {
        bool current = _pendingReplies.TryGetValue(conversationId, out CancellationTokenSource? active) && active == pending;

        if (current)
        {
            _pendingReplies.Remove(conversationId);
            _searching.Remove(conversationId);
            _groupSpeakers.Remove(conversationId);
            _openingRounds.Remove(conversationId);
        }

        pending.Dispose();
        return current;
    }

    private void ShowProblem(ConversationId conversationId, string problem)
    {
        Status = problem;

        if (ActiveConversationId == conversationId)
        {
            ReplyProblem = problem;
        }
    }

    /// <summary>
    /// Shows a saved reply in its thread, if open (and speaks it, when speech is on), and in the chat list.
    /// </summary>
    private void ShowReply(ConversationId conversationId, ChatMessage reply)
    {
        if (ActiveConversationId == conversationId)
        {
            AppendMessage(reply);
            // Spoken even when a newer reply has been asked for, since it is shown; replies are voiced in turn.
            Voice(reply);
        }

        ConversationListItem? item = Conversations.FirstOrDefault(existing => existing.Id == conversationId);

        if (item is not null)
        {
            item.Conversation = item.Conversation with { LastMessage = reply };
            MoveToTop(item);
        }
    }

    /// <summary>
    /// Learns lasting facts from older messages as the contacts' memories, in the background after a reply. A reply
    /// asked for meanwhile waits its turn for the model.
    /// </summary>
    private async Task UpdateMemoryAsync(ConversationId conversationId)
    {
        if (!_updatingMemory.Add(conversationId))
        {
            return;
        }

        try
        {
            if (await _conversations.UpdateMemoryAsync(conversationId) is { } updated
                && Conversations.FirstOrDefault(existing => existing.Id == conversationId) is { } item)
            {
                item.Conversation = item.Conversation with { Participants = updated.Participants };
            }
        }
        catch (Exception)
        {
            // Retried after the next reply; until then the contact is still given the messages themselves.
        }
        finally
        {
            _updatingMemory.Remove(conversationId);
        }
    }

    /// <summary>
    /// Shows what the open chat's contact remembers, where the user can correct it. Changes are saved as they are made;
    /// afterwards the changed memories are embedded in the background so they can be found by meaning.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasConversation))]
    private Task EditMemoryAsync()
    {
        return ActiveContact is { } contact ? EditMemoryOfAsync(contact) : Task.CompletedTask;
    }

    /// <summary>
    /// Shows what one of the open group's contacts remembers. Each contact's memories are their own, whichever chat they
    /// were learned in.
    /// </summary>
    [RelayCommand]
    private Task EditGroupMemoryAsync(Contact? contact)
    {
        return contact is not null && IsGroupThread ? EditMemoryOfAsync(contact) : Task.CompletedTask;
    }

    private async Task EditMemoryOfAsync(Contact contact)
    {
        if (ShowMemoryEditorAsync is null || ActiveConversationId is not { } conversationId)
        {
            return;
        }

        MemoryEditorViewModel editor = new(_memories, contact, conversationId);
        await ShowMemoryEditorAsync(editor);

        if (editor.HasChanges)
        {
            Status = $"Saved what {contact.DisplayName} remembers.";
            _ = EmbedMemoriesAsync(contact.Id);
        }
    }

    private async Task EmbedMemoriesAsync(ContactId contactId)
    {
        try
        {
            await _memories.EmbedPendingAsync(contactId);
        }
        catch (Exception)
        {
            // Retried after the next reply; until then the memories are found by their words.
        }
    }

    private void ShowActivity(ConversationId conversationId, CancellationTokenSource pending, ReplyActivity activity)
    {
        // A superseded reply's late report must not change what the current one shows.
        if (!_pendingReplies.TryGetValue(conversationId, out CancellationTokenSource? active) || active != pending)
        {
            return;
        }

        if (activity == ReplyActivity.SearchingWeb)
        {
            _searching.Add(conversationId);
        }
        else
        {
            _searching.Remove(conversationId);
        }

        UpdateTyping();
    }

    /// <summary>
    /// Whether someone in the conversation is writing: its contact replying, or one of a group's contacts taking their
    /// turn (a group deciding whether anyone writes shows nothing).
    /// </summary>
    private bool IsTyping(ConversationId conversationId)
    {
        if (!_pendingReplies.ContainsKey(conversationId))
        {
            return false;
        }

        bool isGroup = ActiveGroup?.Id == conversationId || Conversations.FirstOrDefault(item => item.Id == conversationId)?.IsGroup == true;

        return !isGroup || _groupSpeakers.ContainsKey(conversationId);
    }

    private void UpdateTyping()
    {
        IsContactTyping = ActiveConversationId is { } active && IsTyping(active);
        CanStopReplies = IsReplyPending;
        NotifyCanContinue();
        IsContactSearching = IsContactTyping && _searching.Contains(ActiveConversationId!.Value);
        OnPropertyChanged(nameof(TypingContact));
        OnPropertyChanged(nameof(ActiveSubtitle));

        foreach (ConversationListItem item in Conversations)
        {
            item.IsTyping = IsTyping(item.Id);
        }
    }

    /// <summary>
    /// Deletes <paramref name="item"/>, or the open chat when none is given, after the user confirms.
    /// </summary>
    [RelayCommand]
    private async Task DeleteConversationAsync(ConversationListItem? item)
    {
        item ??= Conversations.FirstOrDefault(existing => existing.Id == ActiveConversationId);

        if (item is null)
        {
            Status = "Select a chat to delete.";
            return;
        }

        string question = item.IsGroup
            ? $"Delete the group {item.Title}? What its contacts learned in it is forgotten. Your own chats with them are kept. This cannot be undone."
            : $"Delete your chat with {item.Contact.DisplayName}? This cannot be undone.";

        if (ConfirmAsync is not null && !await ConfirmAsync(item.IsGroup ? "Delete group" : "Delete chat", question))
        {
            return;
        }

        try
        {
            await _conversations.DeleteAsync(item.Id);
        }
        catch (Exception error)
        {
            Status = "Could not delete: " + error.Message;
            return;
        }

        CancelReply(item.Id);
        Conversations.Remove(item);
        VisibleConversations.Remove(item);
        HasConversations = Conversations.Count > 0;

        if (ActiveConversationId == item.Id)
        {
            CloseThread();
        }

        _drafts.Remove(item.IsGroup ? item.Id.Value : item.Contact.Id.Value);

        Status = item.IsGroup ? "Group deleted." : "Chat deleted.";
    }

    private string CountStatus()
    {
        return Conversations.Count switch
        {
            0 when Contacts.Count == 0 => "No contacts yet. Add a persona on the Contacts page to start chatting.",
            0 => "No chats yet. Start one with the new chat button.",
            1 => "1 chat",
            _ => $"{Conversations.Count} chats"
        };
    }
}
