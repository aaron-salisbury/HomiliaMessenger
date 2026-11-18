using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Memories;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HomiliaMessenger.Business.Modules.Conversations;

/// <summary>
/// What the coordinator weighs to choose who writes next in a group.
/// </summary>
/// <param name="Contacts">The group's contacts, in the order they joined.</param>
/// <param name="Messages">The group's messages, oldest first.</param>
/// <param name="MemoryMatches">
/// For each contact, how many words of the latest message appear in what that contact remembers. Each contact's count
/// comes from their own memories only.
/// </param>
internal sealed record GroupSituation(
    Conversation Conversation,
    IReadOnlyList<Contact> Contacts,
    IReadOnlyList<ChatMessage> Messages,
    GroupCycle Cycle,
    DateTimeOffset Now,
    IReadOnlyDictionary<ContactId, int>? MemoryMatches = null);

/// <summary>
/// The coordinator's choice. A <see cref="Chance"/> below one means nothing calls for the speaker to write, but they might
/// want to: whether they do is then left to their model, when it can be asked cheaply, or to chance.
/// </summary>
internal sealed record GroupDecision(ContactId? Speaker, GroupTurnReason Reason, double Chance = 1)
{
    internal static readonly GroupDecision Wait = new(null, GroupTurnReason.Answering, 0);

    internal bool IsCertain => Speaker is not null && Chance >= 1;
}

/// <summary>
/// Decides who in a group writes next, if anyone, so a group reads like friends talking rather than every contact
/// answering every message.
/// </summary>
/// <remarks>
/// <para>
/// The hard limits are deterministic: only the group's contacts write, never the same contact twice in a row, at most
/// <see cref="GroupCoordinatorOptions.MaxAutomaticTurns"/> messages per round, a contact spoken to by name answers, and
/// a group starts a conversation on its own at most once per <see cref="GroupCoordinatorOptions.InitiationCooldown"/>.
/// </para>
/// <para>
/// Within those limits, the first reply to the user goes to whoever the message bears on most: what they remember, their
/// personality and background, and who has done less of the talking lately. Whether another contact then adds something
/// is the soft decision: certain when the message was for everyone or the reply spoke to them, otherwise weighted by
/// their personality and share of the conversation, and left to their model or to chance (see <see cref="GroupDecision"/>).
/// </para>
/// </remarks>
internal sealed partial class GroupCoordinator
{
    private static readonly string[] TALKATIVE = ["talkative", "chatty", "outgoing", "extrovert", "opinionated", "enthusiastic", "curious", "loud", "energetic", "bubbly", "sociable", "witty", "playful", "argumentative"];
    private static readonly string[] RESERVED = ["quiet", "shy", "reserved", "introvert", "laconic", "taciturn", "private", "aloof", "stoic", "terse", "withdrawn"];

    private readonly GroupCoordinatorOptions _options;
    private readonly Random _random;
    private readonly object _randomLock = new();

    public GroupCoordinator(GroupCoordinatorOptions options) : this(options, Random.Shared)
    {
    }

    internal GroupCoordinator(GroupCoordinatorOptions options, Random random)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _random = random ?? throw new ArgumentNullException(nameof(random));
    }

    internal GroupCoordinatorOptions Options => _options;

    /// <summary>
    /// A number in [0, 1), for decisions left to chance.
    /// </summary>
    internal double Roll()
    {
        lock (_randomLock)
        {
            return _random.NextDouble();
        }
    }

    internal GroupDecision Decide(GroupSituation situation)
    {
        ArgumentNullException.ThrowIfNull(situation);

        if (situation.Cycle.TurnsTaken >= _options.MaxAutomaticTurns || situation.Contacts.Count == 0)
        {
            return GroupDecision.Wait;
        }

        if (situation.Cycle.Trigger == GroupTrigger.Opened && situation.Cycle.TurnsTaken == 0)
        {
            return DecideOpening(situation);
        }

        // Until someone has written, including after a chosen contact could not.
        if (situation.Cycle.Trigger == GroupTrigger.Continued && WrittenThisRound(situation.Cycle) == 0)
        {
            return DecideContinuation(situation);
        }

        ChatMessage? last = situation.Messages.LastOrDefault();

        if (last is null)
        {
            return GroupDecision.Wait;
        }

        ContactId? lastSender = situation.Conversation.SenderOf(last);

        return lastSender is null ? AnswerUser(situation, last) : FollowContact(situation, lastSender.Value, last);
    }

    /// <summary>
    /// Whether the group may start a conversation now, before chance decides whether it does: initiation is on, the
    /// cooldown has passed, the group has been quiet for a while, and the last thing said is not a contact's question
    /// still waiting for the user. A new group with no messages may always say hello.
    /// </summary>
    internal bool MayInitiate(Conversation conversation, IReadOnlyList<ChatMessage> messages, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(messages);

        if (!conversation.IsGroup || conversation.Initiation == GroupInitiation.Off)
        {
            return false;
        }

        if (conversation.LastInitiationAtUtc is { } lastChance && now - lastChance < _options.InitiationCooldown)
        {
            return false;
        }

        if (messages.Count == 0)
        {
            return true;
        }

        ChatMessage last = messages[^1];

        // The user's own message waiting for an answer, or a contact's question waiting for the user's: either way there
        // is an open thread, and starting another would talk over it.
        if (last.Sender == MessageSender.User || last.Text.Contains('?'))
        {
            return false;
        }

        return now - last.SentAtUtc >= _options.InitiationQuietPeriod;
    }

    private GroupDecision DecideOpening(GroupSituation situation)
    {
        if (!MayInitiate(situation.Conversation, situation.Messages, situation.Now))
        {
            return GroupDecision.Wait;
        }

        if (situation.Messages.Count == 0)
        {
            // The contact just invited says hello; the one the user was already talking with may then join in.
            return new GroupDecision(situation.Contacts[^1].Id, GroupTurnReason.Greeting);
        }

        // Whoever is likelier to speak up starts it, with some variety.
        Contact starter = situation.Contacts
            .OrderByDescending(contact => Talkativeness(contact) + Roll() - (0.5 * Share(situation, contact.Id)))
            .First();

        return new GroupDecision(starter.Id, GroupTurnReason.Initiating, _options.InitiationChance);
    }

    /// <summary>
    /// The first turn when the user asks the group to keep talking: whoever has most reason to write next does, chosen as
    /// any next turn is (spoken to by name, otherwise by personality and share of the talking, never the contact who just
    /// wrote), except that someone always writes, since the user asked. They are told to keep the conversation going, even
    /// when spoken to by name, since the user has written nothing new; only a message of the user's still waiting for an
    /// answer is answered as usual. When nobody else can write (say, their model failed), the contact who just wrote
    /// continues.
    /// </summary>
    private GroupDecision DecideContinuation(GroupSituation situation)
    {
        ChatMessage? last = situation.Messages.LastOrDefault();

        if (last is null)
        {
            return GroupDecision.Wait;
        }

        if (situation.Conversation.SenderOf(last) is not { } lastSender)
        {
            // The user's own message is still waiting for an answer.
            return AnswerUser(situation, last);
        }

        if (FollowContact(situation, lastSender, last).Speaker is { } speaker)
        {
            return new GroupDecision(speaker, GroupTurnReason.Continuing);
        }

        return situation.Contacts.Any(contact => contact.Id == lastSender)
            ? new GroupDecision(lastSender, GroupTurnReason.Continuing)
            : GroupDecision.Wait;
    }

    /// <summary>
    /// How many messages contacts have written in the round: its turns, less those whose contact could not write.
    /// </summary>
    private static int WrittenThisRound(GroupCycle cycle)
    {
        return Math.Max(0, cycle.TurnsTaken - cycle.Unavailable.Count);
    }

    private GroupDecision AnswerUser(GroupSituation situation, ChatMessage message)
    {
        IReadOnlyList<Contact> addressed = Addressed(message.Text, situation.Contacts);

        if (addressed.Count > 0)
        {
            return new GroupDecision(addressed[0].Id, GroupTurnReason.Addressed);
        }

        Contact speaker = situation.Contacts.OrderByDescending(contact => Relevance(situation, contact, message)).First();

        return new GroupDecision(speaker.Id, GroupTurnReason.Answering);
    }

    private GroupDecision FollowContact(GroupSituation situation, ContactId lastSender, ChatMessage last)
    {
        List<Contact> others = [.. situation.Contacts.Where(contact => contact.Id != lastSender)];

        if (others.Count == 0)
        {
            return GroupDecision.Wait;
        }

        // A contact spoken to by name answers, even another contact.
        IReadOnlyList<Contact> addressed = Addressed(last.Text, others);

        if (addressed.Count > 0)
        {
            return new GroupDecision(addressed[0].Id, GroupTurnReason.Addressed);
        }

        int roundStart = RoundStart(situation);
        HashSet<ContactId> spoken = [.. situation.Messages.Skip(roundStart).Select(situation.Conversation.SenderOf).OfType<ContactId>()];
        ChatMessage? userMessage = roundStart > 0 && situation.Messages[roundStart - 1].Sender == MessageSender.User ? situation.Messages[roundStart - 1] : null;

        // The user's message was for everyone, or named someone who has not answered yet.
        if (userMessage is not null)
        {
            bool forEveryone = AddressesEveryone(userMessage.Text);
            IReadOnlyList<Contact> named = Addressed(userMessage.Text, situation.Contacts);
            Contact? owed = others.FirstOrDefault(contact => !spoken.Contains(contact.Id) && (forEveryone || named.Any(name => name.Id == contact.Id)));

            if (owed is not null)
            {
                return new GroupDecision(owed.Id, GroupTurnReason.Answering);
            }
        }

        // Someone who has not yet spoken this round may add something; once everyone has, the round is over unless a
        // contact was spoken to by name.
        Contact? candidate = others.Where(contact => !spoken.Contains(contact.Id)).OrderByDescending(contact => Talkativeness(contact) - Share(situation, contact.Id)).FirstOrDefault();

        if (candidate is null)
        {
            return GroupDecision.Wait;
        }

        double chance = _options.FollowUpChance * (1 + (0.5 * Talkativeness(candidate))) * (1.25 - Share(situation, candidate.Id));

        // A question left open invites an answer.
        if (last.Text.Contains('?'))
        {
            chance += 0.15;
        }

        return new GroupDecision(candidate.Id, GroupTurnReason.FollowingUp, Math.Clamp(chance, 0.05, 0.9));
    }

    /// <summary>
    /// Where the current round's messages start: after the user's latest message when it started the round, otherwise
    /// the messages written in this round.
    /// </summary>
    private static int RoundStart(GroupSituation situation)
    {
        if (situation.Cycle.Trigger is GroupTrigger.Opened or GroupTrigger.Continued)
        {
            return Math.Max(0, situation.Messages.Count - WrittenThisRound(situation.Cycle));
        }

        for (int index = situation.Messages.Count - 1; index >= 0; index--)
        {
            if (situation.Messages[index].Sender == MessageSender.User)
            {
                return index + 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// How much <paramref name="message"/> bears on <paramref name="contact"/>: what they remember of it counts most, then
    /// their personality and background, then a share of the talking they have not had, with a little variety.
    /// </summary>
    private double Relevance(GroupSituation situation, Contact contact, ChatMessage message)
    {
        HashSet<string> words = MemoryRecall.Words(message.Text);
        int remembered = situation.MemoryMatches?.GetValueOrDefault(contact.Id) ?? 0;
        int persona = MemoryRecall.Words($"{contact.Personality} {contact.Background} {contact.Relationship}").Count(words.Contains);

        return (2.0 * Math.Min(remembered, 3)) + Math.Min(persona, 3) + (0.5 * Talkativeness(contact)) - (2.0 * Share(situation, contact.Id)) + (0.75 * Roll());
    }

    /// <summary>
    /// The share of the contacts' recent messages that <paramref name="contactId"/> wrote, from 0 to 1.
    /// </summary>
    private double Share(GroupSituation situation, ContactId contactId)
    {
        List<ContactId> recent = [.. situation.Messages.TakeLast(_options.ParticipationWindow).Select(situation.Conversation.SenderOf).OfType<ContactId>()];

        return recent.Count == 0 ? 0 : (double)recent.Count(id => id == contactId) / recent.Count;
    }

    /// <summary>
    /// From -1 for a reserved personality to 1 for a talkative one, read from words in the contact's description.
    /// </summary>
    internal static double Talkativeness(Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        string description = $"{contact.Personality} {contact.Background}".ToLowerInvariant();
        int talkative = TALKATIVE.Count(word => description.Contains(word, StringComparison.Ordinal));
        int reserved = RESERVED.Count(word => description.Contains(word, StringComparison.Ordinal));

        return Math.Clamp((talkative - reserved) / 2.0, -1, 1);
    }

    /// <summary>
    /// The contacts <paramref name="text"/> speaks to by name, in the order they are named: a name opening a sentence
    /// ("Marcus, what do you think?", "hey marcus"), set off by commas as someone spoken to ("what about you, Elena?",
    /// "no, Marcus, you're wrong"), or written with "@". A name merely mentioned ("I asked Marcus", "did Marcus tell
    /// you?", "I told Marcus, and he laughed") does not count.
    /// </summary>
    internal static IReadOnlyList<Contact> Addressed(string text, IReadOnlyList<Contact> contacts)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(contacts);

        string trimmed = text.Trim();
        List<(Contact Contact, int Index)> found = [];

        foreach (Contact contact in contacts)
        {
            foreach (string name in Names(contact))
            {
                Regex pattern = new($@"(?<![\p{{L}}\p{{N}}])@?{Regex.Escape(name)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                foreach (Match match in pattern.Matches(trimmed))
                {
                    string before = trimmed[..match.Index].TrimEnd();
                    string after = trimmed[(match.Index + match.Length)..].TrimStart();
                    bool opensSentence = before.Length == 0 || before[^1] is '.' or '!' or '?' || GreetingEnd().IsMatch(before);
                    bool spokenTo = before.EndsWith(',') && (after.Length == 0 || after[0] is ',' or '?' or '!' or '.');

                    if (opensSentence || spokenTo || match.Value.StartsWith('@'))
                    {
                        found.Add((contact, match.Index));
                        break;
                    }
                }

                if (found.Any(item => item.Contact.Id == contact.Id))
                {
                    break;
                }
            }
        }

        return [.. found.OrderBy(item => item.Index).Select(item => item.Contact)];
    }

    /// <summary>
    /// Whether <paramref name="text"/> is for the whole group, such as "what do you guys think?" or "both of you".
    /// </summary>
    internal static bool AddressesEveryone(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return EveryonePattern().IsMatch(text);
    }

    private static IEnumerable<string> Names(Contact contact)
    {
        return new[] { contact.DisplayName, contact.Nickname, contact.GivenName }
            .Where(name => !string.IsNullOrWhiteSpace(name) && name.Trim().Length >= 2)
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\b(you (two|guys|both|all)|both of you|y'?all|everyone|everybody|anyone|anybody|guys|you lot)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EveryonePattern();

    // Text ending with a greeting that leads into a name, such as "hey" or "ok so,".
    [GeneratedRegex(@"(^|[\s.!?])(hey|hi|hello|yo|ok|okay|so|well|and|but)\s*,?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GreetingEnd();
}
