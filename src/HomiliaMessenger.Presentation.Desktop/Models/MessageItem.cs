using CommunityToolkit.Mvvm.ComponentModel;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.WebSearch;
using System;
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>
    /// Whether the continue-conversation button shows beneath this message's avatar: only on the thread's latest message
    /// from a contact.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContinuesDirectChat), nameof(ContinuesGroup))]
    private bool _showsContinue;

    private MessageItem(ChatMessage message, bool isFirstInGroup, string? timeSeparator, Contact? sender, bool namesSender)
    {
        Message = message;
        IsFirstInGroup = isFirstInGroup;
        TimeSeparator = timeSeparator;
        Sender = sender;
        _namesSender = namesSender;
        string sentAt = MessengerTime.ForToolTip(message.SentAtUtc);
        SentAtToolTip = message.Agent is { } agent ? $"{sentAt} · {agent.Provider} · {agent.Model}" : sentAt;
        Sources = [.. (message.WebSearch?.Sources ?? []).Select(WebSourceLink.From).OfType<WebSourceLink>()];
        LookedUp = message.WebSearch is { } lookup ? $"Looked up “{lookup.Query}”" : null;
    }

    public ChatMessage Message { get; }
    public MessageId Id => Message.Id;
    public string Text => Message.Text;
    public bool HasText => !string.IsNullOrWhiteSpace(Message.Text);

    /// <summary>
    /// The picture sent with the message, such as a selfie, as a PNG; <see langword="null"/> for text alone.
    /// </summary>
    public byte[]? ImagePng => Message.Image?.Png is { Length: > 0 } png ? png : null;

    public bool HasImage => ImagePng is not null;

    /// <summary>
    /// What the picture shows, in words, as its tooltip and accessible name.
    /// </summary>
    public string? ImageDescription => Message.Image?.Description;
    public bool IsFromUser => Message.Sender == MessageSender.User;
    public bool IsFirstInGroup { get; }

    /// <summary>
    /// Text shown above this message to mark a break in the conversation, or <see langword="null"/>.
    /// </summary>
    public string? TimeSeparator { get; }
    public bool HasTimeSeparator => TimeSeparator is not null;
    public string SentAtToolTip { get; }
    public bool ShowsAvatar => !IsFromUser && IsLastInGroup;

    private readonly bool _namesSender;

    /// <summary>
    /// The contact who wrote the message, whose photo is its avatar; <see langword="null"/> for the user's messages.
    /// </summary>
    public Contact? Sender { get; }

    public byte[]? SenderPhoto => Sender?.Photo;

    public string? SenderName => Sender?.DisplayName;

    /// <summary>
    /// Whether the sender's name is shown above the message: in a group, on the first of each run of their messages.
    /// </summary>
    public bool ShowsSenderName => _namesSender && !IsFromUser && IsFirstInGroup && Sender is not null;

    /// <summary>
    /// Whether this message may carry the continue-conversation button: a contact's message whose sender is known.
    /// </summary>
    internal bool CanCarryContinue => !IsFromUser && Sender is not null;

    /// <summary>
    /// The continue button for a direct chat, which asks the contact straight away.
    /// </summary>
    public bool ContinuesDirectChat => ShowsContinue && !_namesSender;

    /// <summary>
    /// The continue button for a group, which offers a choice of who continues.
    /// </summary>
    public bool ContinuesGroup => ShowsContinue && _namesSender;

    /// <summary>
    /// What the contact searched for before writing this reply, or <see langword="null"/> when nothing was looked up.
    /// </summary>
    public string? LookedUp { get; }

    /// <summary>
    /// Where what was looked up came from.
    /// </summary>
    public IReadOnlyList<WebSourceLink> Sources { get; }

    public bool HasSources => Sources.Count > 0;

    /// <summary>
    /// Creates the item for <paramref name="message"/> as the one following <paramref name="previous"/>, ending the
    /// previous item's group when this message continues it.
    /// </summary>
    /// <param name="sender">The contact who wrote <paramref name="message"/>, or <see langword="null"/> for the user.</param>
    /// <param name="namesSender">Whether to show the sender's name, as a group chat does.</param>
    internal static MessageItem Follow(MessageItem? previous, ChatMessage message, DateTimeOffset now, Contact? sender = null, bool namesSender = false)
    {
        ArgumentNullException.ThrowIfNull(message);

        TimeSpan gap = previous is null ? TimeSpan.MaxValue : message.SentAtUtc - previous.Message.SentAtUtc;
        bool newDay = previous is null || message.SentAtUtc.ToLocalTime().Date != previous.Message.SentAtUtc.ToLocalTime().Date;
        bool separated = newDay || gap > SEPARATOR_GAP;
        bool startsGroup = separated || previous!.Message.Sender != message.Sender || previous.Sender?.Id != sender?.Id || gap > GROUP_GAP;

        if (!startsGroup)
        {
            previous!.IsLastInGroup = false;
        }

        return new MessageItem(message, startsGroup, separated ? MessengerTime.ForSeparator(message.SentAtUtc, now) : null, message.Sender == MessageSender.User ? null : sender, namesSender);
    }
}

/// <summary>
/// A page a reply's looked-up information came from, as a link.
/// </summary>
public sealed record WebSourceLink(string Title, string Site, Uri Uri)
{
    /// <summary>
    /// The link for a search result, or <see langword="null"/> unless its address is <c>http</c> or <c>https</c>.
    /// </summary>
    internal static WebSourceLink? From(WebSearchResult result)
    {
        if (!Uri.TryCreate(result.Url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        string site = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        string title = string.IsNullOrWhiteSpace(result.Title) ? site : result.Title.ReplaceLineEndings(" ").Trim();

        return new WebSourceLink(title, result.PublishedAt is { } published ? $"{site} · {published:MMM d, yyyy}" : site, uri);
    }
}
