using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Base.Extensions;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Presentation.Desktop.Base.Controls.RibbonControls;
using HomiliaMessenger.Presentation.Desktop.Models;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.ViewModels;

public partial class MessengerViewModel : BaseViewModel, IRibbonProvider
{
    private readonly IConversationService _conversations;
    private readonly IContactService _contacts;

    // Conversations whose contact is replying. The value records whether the user wrote again meanwhile, in which case
    // the contact replies once more when done, as a person catching up on a burst of messages would.
    private readonly Dictionary<ConversationId, bool> _pendingReplies = [];

    // Unsent composer text per contact, kept while switching between chats.
    private readonly Dictionary<ContactId, string> _drafts = [];

    private bool _syncingListSelection;
    private int _threadVersion;

    /// <summary>
    /// The conversation list's selected item. Selecting a conversation opens its thread; clearing the selection (as
    /// filtering does) leaves the open thread alone.
    /// </summary>
    [ObservableProperty] private ConversationListItem? _selectedConversation;

    /// <summary>
    /// Who the open thread is with, or <see langword="null"/> when no thread is open.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveThread), nameof(ActiveSubtitle))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private Contact? _activeContact;

    /// <summary>
    /// The open thread's conversation, or <see langword="null"/> for a new chat that has no messages yet.
    /// </summary>
    [ObservableProperty] private ConversationId? _activeConversationId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draftText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveSubtitle))]
    private bool _isContactTyping;

    [ObservableProperty] private bool _hasMessages;
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
    /// Supplied by the view to ask the user to confirm an action, given a title and a message.
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public Type RibbonControlType => typeof(MessengerRibbonControl);

    public bool HasActiveThread => ActiveContact is not null;

    public string ActiveSubtitle => IsContactTyping
        ? "typing…"
        : ActiveContact?.Agent is { } agent ? $"{agent.Provider} · {agent.Model}" : "Simulated replies";

    public MessengerViewModel(IConversationService conversations, IContactService contacts)
    {
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
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
                if (contactsById.TryGetValue(conversation.ContactId, out Contact? contact))
                {
                    Conversations.Add(new ConversationListItem(conversation, contact) { IsTyping = _pendingReplies.ContainsKey(conversation.Id) });
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
    /// Brings the open thread up to date after a reload: closes it if its contact was deleted, and otherwise reloads it
    /// so edits to the contact (such as a new photo) show.
    /// </summary>
    private async Task RefreshActiveThreadAsync(Dictionary<ContactId, Contact> contactsById, IReadOnlyList<Conversation> conversations)
    {
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
            || item.Contact.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
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

        _ = OpenThreadAsync(value.Contact, value.Id);
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

        ConversationListItem? listed = Conversations.FirstOrDefault(item => item.Contact.Id == contact.Id);

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

    private async Task OpenThreadAsync(Contact contact, ConversationId? conversationId, bool reloadSameThread = false)
    {
        bool sameThread = ActiveContact?.Id == contact.Id;

        if (!sameThread)
        {
            if (ActiveContact is not null)
            {
                _drafts[ActiveContact.Id] = DraftText;
            }

            DraftText = _drafts.GetValueOrDefault(contact.Id, string.Empty);
        }
        else if (!reloadSameThread && ActiveConversationId == conversationId)
        {
            return;
        }

        int version = ++_threadVersion;
        ActiveContact = contact;
        ActiveConversationId = conversationId;
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

        try
        {
            IReadOnlyList<ChatMessage> messages = await _conversations.ListMessagesAsync(conversationId.Value);

            // The user may have switched chats while the messages loaded.
            if (version != _threadVersion)
            {
                return;
            }

            Messages.Clear();

            foreach (ChatMessage message in messages)
            {
                AppendMessage(message);
            }

            HasMessages = Messages.Count > 0;
        }
        catch (Exception error)
        {
            Status = "Could not load messages: " + error.Message;
        }
    }

    private void CloseThread()
    {
        _threadVersion++;

        if (ActiveContact is not null)
        {
            _drafts[ActiveContact.Id] = DraftText;
        }

        ActiveContact = null;
        ActiveConversationId = null;
        DraftText = string.Empty;
        Messages.Clear();
        HasMessages = false;
        UpdateTyping();
        SyncListSelection();
    }

    private void AppendMessage(ChatMessage message)
    {
        // A reply saved just before a reload is already in the reloaded thread.
        if (Messages.Any(item => item.Id == message.Id))
        {
            return;
        }

        Messages.Add(MessageItem.Follow(Messages.LastOrDefault(), message, DateTimeOffset.Now));
        HasMessages = true;
    }

    private bool CanSend()
    {
        return ActiveContact is not null && !string.IsNullOrWhiteSpace(DraftText);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (ActiveContact is not { } contact || string.IsNullOrWhiteSpace(DraftText))
        {
            return;
        }

        string text = DraftText.Trim();

        // Clear the composer right away, like a messenger does, and restore the text if sending fails.
        DraftText = string.Empty;
        SendMessageResult result;

        try
        {
            result = await _conversations.SendAsync(contact.Id, text);
        }
        catch (Exception error)
        {
            Status = "Could not send: " + error.Message;
            RestoreDraft(contact, text);
            return;
        }

        if (!result.Validation.IsValid || result.Conversation is null || result.Message is null)
        {
            Status = result.Validation.ToStringPretty("Send");
            RestoreDraft(contact, text);
            return;
        }

        if (ActiveContact?.Id == contact.Id)
        {
            ActiveConversationId = result.Conversation.Id;
            AppendMessage(result.Message);
        }

        UpsertListItem(result.Conversation, contact);
        StartReply(result.Conversation.Id);
    }

    private void RestoreDraft(Contact contact, string text)
    {
        if (ActiveContact?.Id == contact.Id)
        {
            if (string.IsNullOrEmpty(DraftText))
            {
                DraftText = text;
            }
        }
        else if (string.IsNullOrEmpty(_drafts.GetValueOrDefault(contact.Id)))
        {
            _drafts[contact.Id] = text;
        }
    }

    private void UpsertListItem(Conversation conversation, Contact contact)
    {
        ConversationListItem? item = Conversations.FirstOrDefault(existing => existing.Id == conversation.Id);

        if (item is null)
        {
            item = new ConversationListItem(conversation, contact);
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

    private void StartReply(ConversationId conversationId)
    {
        if (_pendingReplies.ContainsKey(conversationId))
        {
            _pendingReplies[conversationId] = true;
            return;
        }

        _pendingReplies[conversationId] = false;
        UpdateTyping();
        _ = ReplyAsync(conversationId);
    }

    private async Task ReplyAsync(ConversationId conversationId)
    {
        ChatMessage? reply = null;

        try
        {
            reply = await _conversations.ReplyAsync(conversationId);
        }
        catch (Exception error)
        {
            Status = "The contact could not reply: " + error.Message;
        }

        bool wroteAgain = _pendingReplies.Remove(conversationId, out bool pending) && pending;

        if (reply is not null)
        {
            if (ActiveConversationId == conversationId)
            {
                AppendMessage(reply);
            }

            ConversationListItem? item = Conversations.FirstOrDefault(existing => existing.Id == conversationId);

            if (item is not null)
            {
                item.Conversation = item.Conversation with { LastMessage = reply };
                MoveToTop(item);
            }
        }

        UpdateTyping();

        if (wroteAgain && reply is not null)
        {
            StartReply(conversationId);
        }
    }

    private void UpdateTyping()
    {
        IsContactTyping = ActiveConversationId is { } active && _pendingReplies.ContainsKey(active);

        foreach (ConversationListItem item in Conversations)
        {
            item.IsTyping = _pendingReplies.ContainsKey(item.Id);
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

        if (ConfirmAsync is not null && !await ConfirmAsync("Delete chat", $"Delete your chat with {item.Contact.DisplayName}? This cannot be undone."))
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

        Conversations.Remove(item);
        VisibleConversations.Remove(item);
        HasConversations = Conversations.Count > 0;

        if (ActiveConversationId == item.Id)
        {
            CloseThread();
        }

        _drafts.Remove(item.Contact.Id);

        Status = "Chat deleted.";
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
