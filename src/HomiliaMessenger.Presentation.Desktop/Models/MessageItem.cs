using CommunityToolkit.Mvvm.ComponentModel;
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

    private MessageItem(ChatMessage message, bool isFirstInGroup, string? timeSeparator)
    {
        Message = message;
        IsFirstInGroup = isFirstInGroup;
        TimeSeparator = timeSeparator;
        string sentAt = MessengerTime.ForToolTip(message.SentAtUtc);
        SentAtToolTip = message.Agent is { } agent ? $"{sentAt} · {agent.Provider} · {agent.Model}" : sentAt;
        Sources = [.. (message.WebSearch?.Sources ?? []).Select(WebSourceLink.From).OfType<WebSourceLink>()];
        LookedUp = message.WebSearch is { } lookup ? $"Looked up “{lookup.Query}”" : null;
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
