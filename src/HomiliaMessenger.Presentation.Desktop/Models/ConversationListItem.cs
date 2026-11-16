using CommunityToolkit.Mvvm.ComponentModel;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HomiliaMessenger.Presentation.Desktop.Models;

/// <summary>
/// A row in the conversation list: who the chat is with, a preview of the latest message, and when it was sent. A
/// group shows its contacts' photos together and is named after them.
/// </summary>
public sealed partial class ConversationListItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(LastActivity), nameof(Title), nameof(IsGroup))]
    private Conversation _conversation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private bool _isTyping;

    public ConversationListItem(Conversation conversation, Contact contact) : this(conversation, [contact ?? throw new ArgumentNullException(nameof(contact))])
    {
    }

    public ConversationListItem(Conversation conversation, IReadOnlyList<Contact> contacts)
    {
        _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        ArgumentNullException.ThrowIfNull(contacts);

        if (contacts.Count == 0)
        {
            throw new ArgumentException("A conversation has at least one contact.", nameof(contacts));
        }

        Contacts = contacts;
    }

    public ConversationId Id => Conversation.Id;

    /// <summary>
    /// The contacts in the chat, in the order they joined.
    /// </summary>
    public IReadOnlyList<Contact> Contacts { get; }

    /// <summary>
    /// A direct chat's contact, or a group's first contact.
    /// </summary>
    public Contact Contact => Contacts[0];

    public bool IsGroup => Conversation.IsGroup;

    public string Title => IsGroup ? GroupConversations.DisplayName(Conversation, Contacts) : Contact.DisplayName;

    /// <summary>
    /// A group's second contact, whose photo is shown overlapping the first; <see langword="null"/> for a direct chat.
    /// </summary>
    public Contact? SecondContact => IsGroup && Contacts.Count > 1 ? Contacts[1] : null;

    public string Preview
    {
        get
        {
            if (IsTyping)
            {
                return "typing…";
            }

            return Conversation.LastMessage switch
            {
                null => IsGroup ? "New group" : "No messages yet",
                { Sender: MessageSender.User } message => "You: " + Flatten(message.Text),
                ChatMessage message when IsGroup => $"{SenderName(message)}: {Flatten(message.Text)}",
                ChatMessage message => Flatten(message.Text)
            };
        }
    }

    public string LastActivity => MessengerTime.ForList(Conversation.LastActivityAtUtc, DateTimeOffset.Now);

    private string SenderName(ChatMessage message)
    {
        ContactId? sender = Conversation.SenderOf(message);

        return Contacts.FirstOrDefault(contact => contact.Id == sender)?.DisplayName ?? "Someone";
    }

    private static string Flatten(string text)
    {
        return text.ReplaceLineEndings(" ");
    }
}
