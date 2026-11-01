using CommunityToolkit.Mvvm.ComponentModel;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using System;

namespace HomiliaMessenger.Presentation.Desktop.Models;

/// <summary>
/// A row in the conversation list: who the chat is with, a preview of the latest message, and when it was sent.
/// </summary>
public sealed partial class ConversationListItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(LastActivity))]
    private Conversation _conversation;

    [ObservableProperty]
    private Contact _contact;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private bool _isTyping;

    public ConversationListItem(Conversation conversation, Contact contact)
    {
        _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        _contact = contact ?? throw new ArgumentNullException(nameof(contact));
    }

    public ConversationId Id => Conversation.Id;

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
                null => "No messages yet",
                { Sender: MessageSender.User } message => "You: " + Flatten(message.Text),
                ChatMessage message => Flatten(message.Text)
            };
        }
    }

    public string LastActivity => MessengerTime.ForList(Conversation.LastActivityAtUtc, DateTimeOffset.Now);

    private static string Flatten(string text)
    {
        return text.ReplaceLineEndings(" ");
    }
}
