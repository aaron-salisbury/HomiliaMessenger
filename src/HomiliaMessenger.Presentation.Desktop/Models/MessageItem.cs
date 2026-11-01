using CommunityToolkit.Mvvm.ComponentModel;
using HomiliaMessenger.Business.Modules.Conversations;
using System;

namespace HomiliaMessenger.Presentation.Desktop.Models;

/// <summary>
/// A message bubble in a chat thread. Consecutive messages from the same sender are grouped, as in most messengers:
/// grouped bubbles sit closer together, and the contact's avatar is shown once, beside the last bubble of a group.
/// </summary>
public sealed partial class MessageItem : ObservableObject
{
    /// <summary>
    /// Messages further apart than this start a new group even when the sender is the same.
    /// </summary>
    internal static readonly TimeSpan GROUP_GAP = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Messages further apart than this (or on different days) are separated by a timestamp.
    /// </summary>
    internal static readonly TimeSpan SEPARATOR_GAP = TimeSpan.FromMinutes(30);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsAvatar))]
    private bool _isLastInGroup = true;

    private MessageItem(ChatMessage message, bool isFirstInGroup, string? timeSeparator)
    {
        Message = message;
        IsFirstInGroup = isFirstInGroup;
        TimeSeparator = timeSeparator;
        SentAtToolTip = MessengerTime.ForToolTip(message.SentAtUtc);
    }

    public ChatMessage Message { get; }
    public MessageId Id => Message.Id;
    public string Text => Message.Text;
    public bool IsFromUser => Message.Sender == MessageSender.User;
    public bool IsFirstInGroup { get; }

    /// <summary>
    /// Text shown above this message to mark a break in the conversation, or <see langword="null"/>.
    /// </summary>
    public string? TimeSeparator { get; }
    public bool HasTimeSeparator => TimeSeparator is not null;
    public string SentAtToolTip { get; }
    public bool ShowsAvatar => !IsFromUser && IsLastInGroup;

    /// <summary>
    /// Creates the item for <paramref name="message"/> as the one following <paramref name="previous"/>, ending the
    /// previous item's group when this message continues it.
    /// </summary>
    internal static MessageItem Follow(MessageItem? previous, ChatMessage message, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(message);

        TimeSpan gap = previous is null ? TimeSpan.MaxValue : message.SentAtUtc - previous.Message.SentAtUtc;
        bool newDay = previous is null || message.SentAtUtc.ToLocalTime().Date != previous.Message.SentAtUtc.ToLocalTime().Date;
        bool separated = newDay || gap > SEPARATOR_GAP;
        bool startsGroup = separated || previous!.Message.Sender != message.Sender || gap > GROUP_GAP;

        if (!startsGroup)
        {
            previous!.IsLastInGroup = false;
        }

        return new MessageItem(message, startsGroup, separated ? MessengerTime.ForSeparator(message.SentAtUtc, now) : null);
    }
}
