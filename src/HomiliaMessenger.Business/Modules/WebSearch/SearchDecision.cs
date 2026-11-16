using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Conversations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HomiliaMessenger.Business.Modules.WebSearch;

/// <summary>
/// How much a message calls for a web search.
/// </summary>
internal enum SearchNeed
{
    /// <summary>
    /// Ordinary conversation: the contact answers from what they know and remember.
    /// </summary>
    None,

    /// <summary>
    /// The user asked for something to be looked up.
    /// </summary>
    Explicit,

    /// <summary>
    /// A question about something recent or likely to have changed, such as a new release.
    /// </summary>
    Current,

    /// <summary>
    /// A question about a specific named thing that may or may not need a search; a model decides.
    /// </summary>
    Uncertain
}

/// <summary>
/// Whether to search for a message, and with what query.
/// </summary>
/// <param name="Query">What to search for, or <see langword="null"/> when nothing safe and useful could be made.</param>
internal sealed record SearchPlan(SearchNeed Need, string? Query, bool TimeSensitive)
{
    internal static SearchPlan Nothing { get; } = new(SearchNeed.None, null, false);
}

/// <summary>
/// Decides whether a message calls for a web search, before the contact replies, without relying on a model's tool
/// calling, which the local models do not reliably support.
/// </summary>
/// <remarks>
/// <para>
/// Rules recognise explicit requests ("can you look that up?") and questions about recent things ("is the new MonoGame
/// out yet?"). A question about a specific named thing with no sign of recency ("have you played Hollow Knight?") is
/// uncertain, and a model is asked a constrained yes-or-no question (see <see cref="ModelRequest"/>). Everything else,
/// including all statements, small talk, and questions about the contact or the user, is answered without searching.
/// Mentioning a current topic is not enough: a message must ask about it.
/// </para>
/// <para>
/// The query is always made here, from the user's latest message (or, for "look it up", the message before it) with
/// names, contact details, and filler removed, never from the conversation, memories, or profiles, and never by a model.
/// </para>
/// </remarks>
internal static partial class SearchDecision
{
    internal const int MAX_QUERY_WORDS = 12;
    internal const int MAX_QUERY_CHARACTERS = 100;

    /// <summary>
    /// Decides for the user's latest message in <paramref name="recent"/>.
    /// </summary>
    /// <param name="privateNames">Names of the user and the contact, which are never sent.</param>
    internal static SearchPlan Assess(IReadOnlyList<ChatMessage> recent, IReadOnlyCollection<string> privateNames, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(recent);
        ArgumentNullException.ThrowIfNull(privateNames);

        // Only a message the contact has yet to answer can call for a search.
        if (recent.Count == 0 || recent[^1].Sender != MessageSender.User)
        {
            return SearchPlan.Nothing;
        }

        // A burst of messages is answered together; the latest decides.
        string latest = recent[^1].Text;

        // "Don't look it up" is a request too: nothing about this message leaves the device.
        if (DeclinedSearch().IsMatch(latest))
        {
            return SearchPlan.Nothing;
        }

        if (ExplicitRequest().Match(latest) is { Success: true } request)
        {
            string subject = Subject(latest, request);

            // "Look it up!" refers to what the user said before, as long as it was the user who said it.
            if (!HasSubstance(subject, privateNames))
            {
                string? earlier = recent.Take(recent.Count - 1).LastOrDefault(message => message.Sender == MessageSender.User)?.Text;
                // Only a question from it, never whatever else that message said.
                subject = earlier is null ? string.Empty : Sentences(earlier).LastOrDefault(sentence => sentence.EndsWith('?')) ?? string.Empty;
            }

            string? query = BuildQuery(subject, privateNames);
            return new SearchPlan(SearchNeed.Explicit, query, IsTimeSensitive(subject, today));
        }

        string sentence = FocusSentence(latest);

        if (!IsQuestion(sentence) || AboutUs().IsMatch(sentence))
        {
            return SearchPlan.Nothing;
        }

        bool recency = IsTimeSensitive(sentence, today);
        bool namedSubject = HasNamedSubject(sentence, privateNames);

        if (recency && (namedSubject || TopicNoun().IsMatch(sentence)))
        {
            return new SearchPlan(SearchNeed.Current, BuildQuery(sentence, privateNames), true);
        }

        return namedSubject
            ? new SearchPlan(SearchNeed.Uncertain, BuildQuery(sentence, privateNames), false)
            : SearchPlan.Nothing;
    }

    /// <summary>
    /// A small, precise request asking a model only whether a search would help, given the latest message alone. The
    /// answer is one word (a newline ends it), and anything but a clear "yes" means no.
    /// </summary>
    internal static AgentRequest ModelRequest(string message, string model, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(message);

        string instructions = $"""
            You decide whether a quick web search would help answer a text message. Today is {today:MMMM d, yyyy}.
            Answer YES only if the message asks about something public that may be new, recent, or changed lately, or about a specific named product, game, film, show, book, event, or public figure that a well-read person might not know well.
            Answer NO for small talk, feelings, personal life, plans, opinions, advice, or common knowledge.
            Reply with exactly one word: YES or NO.
            """;

        return new AgentRequest(model, instructions, [new AgentTurn(MessageSender.User, message.ReplaceLineEndings(" ").Trim())], ["\n", "\r", ".", ","], Precise: true);
    }

    /// <summary>
    /// Whether a model's answer to <see cref="ModelRequest"/> is a clear yes. Anything else, including no answer, is no.
    /// </summary>
    internal static bool IsYes(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        string word = new([.. answer.Trim().TakeWhile(char.IsLetter)]);

        return string.Equals(word, "yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A short query from <paramref name="text"/>: without addresses, contact details, long numbers, private names,
    /// requests and filler, at most <see cref="MAX_QUERY_WORDS"/> words. Returns <see langword="null"/> when too little
    /// is left to search for.
    /// </summary>
    internal static string? BuildQuery(string text, IReadOnlyCollection<string> privateNames)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(privateNames);

        string query = text.ReplaceLineEndings(" ");
        query = Email().Replace(query, " ");
        query = Link().Replace(query, " ");
        query = Phone().Replace(query, " ");
        query = LongNumber().Replace(query, " ");

        foreach (string name in NameParts(privateNames))
        {
            query = Regex.Replace(query, $@"\b{Regex.Escape(name)}('s)?\b", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        query = RequestPhrase().Replace(query, " ");
        query = Filler().Replace(query, " ");
        query = Unsafe().Replace(query, " ");

        List<string> words = [.. query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim('-', '.', ':', '\'', '"'))
            .Where(word => word.Length > 0)];

        StringBuilder builder = new();

        foreach (string word in words.Take(MAX_QUERY_WORDS))
        {
            if (builder.Length + word.Length + 1 > MAX_QUERY_CHARACTERS)
            {
                break;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(word);
        }

        string result = builder.ToString();

        return result.Count(char.IsLetterOrDigit) >= 3 && !IsOnlyPronouns(result) ? result : null;
    }

    internal static bool IsTimeSensitive(string text, DateOnly today)
    {
        if (Recency().IsMatch(text))
        {
            return true;
        }

        foreach (Match year in Year().Matches(text))
        {
            if (int.Parse(year.Value, System.Globalization.CultureInfo.InvariantCulture) >= today.Year - 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What an explicit request asks to look up, from the sentence holding the request alone: the words after "look up"
    /// or "search for" up to the end of that sentence, or else the words before it in that sentence. Other sentences of
    /// the message are never sent.
    /// </summary>
    private static string Subject(string message, Match request)
    {
        // Splitting keeps an empty part at a sentence break, so a break right beside the request leaves nothing.
        string after = SentenceBreak().Split(message[(request.Index + request.Length)..])[0].Trim();

        return after.Trim('?', '!', '.', ',', ' ').Length > 0
            ? after
            : SentenceBreak().Split(message[..request.Index])[^1].Trim();
    }

    private static bool HasSubstance(string subject, IReadOnlyCollection<string> privateNames)
    {
        return BuildQuery(subject, privateNames) is not null;
    }

    /// <summary>
    /// The sentence that asks something, preferring the last question, so the rest of a long message is never sent.
    /// </summary>
    private static string FocusSentence(string text)
    {
        IReadOnlyList<string> sentences = Sentences(text);

        if (sentences.Count == 0)
        {
            return string.Empty;
        }

        return sentences.LastOrDefault(sentence => sentence.EndsWith('?')) ?? sentences[^1];
    }

    /// <summary>
    /// The sentences of <paramref name="text"/>, split after ".", "!", or "?" followed by a space, and at line breaks.
    /// </summary>
    private static IReadOnlyList<string> Sentences(string text)
    {
        return [.. SentenceBreak().Split(text).Select(sentence => sentence.Trim()).Where(sentence => sentence.Length > 0)];
    }

    private static bool IsQuestion(string sentence)
    {
        return sentence.TrimEnd().EndsWith('?') || QuestionStart().IsMatch(sentence);
    }

    /// <summary>
    /// Whether a word after the first is capitalized and is not a private name, a family title, a day, or "I": a sign
    /// the message is about a specific named thing. "Have you played Hollow Knight?" has one; "how was your day?" does not.
    /// </summary>
    private static bool HasNamedSubject(string sentence, IReadOnlyCollection<string> privateNames)
    {
        HashSet<string> excluded = new(NameParts(privateNames), StringComparer.OrdinalIgnoreCase);
        string[] words = [.. sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => word.Trim('?', '!', '.', ',', ';', ':', '"', '\'', '(', ')'))];

        for (int index = 1; index < words.Length; index++)
        {
            string word = words[index];

            if (word.Length > 1 && char.IsUpper(word[0]) && !excluded.Contains(word) && !CommonCapitalized().IsMatch(word))
            {
                return true;
            }

            // Names with digits, such as "PS5" or "GTA 6", count too.
            if (word.Length > 1 && word.Any(char.IsDigit) && word.Any(char.IsUpper))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> NameParts(IReadOnlyCollection<string> privateNames)
    {
        return privateNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .SelectMany(name => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Append(name.Trim()))
            .Where(part => part.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(part => part.Length);
    }

    private static bool IsOnlyPronouns(string query)
    {
        return query.Split(' ').All(word => word.ToLowerInvariant() is "it" or "that" or "this" or "them" or "those" or "these" or "one" or "thing" or "stuff" or "something" or "out" or "up");
    }

    // A request to the contact, never a statement about searching such as "I often search for…" or "I was going to look
    // it up": asked of them ("can you look it up?", "could you check online…", "what does the internet say about…"), or
    // an instruction that starts a sentence or clause ("Look it up!", "Anyway, search for…", "please google it").
    [GeneratedRegex(@"(?:\b(?:can|could|would|will)\s+you\s+(?:please\s+)?(?:search|google|look\s+(?:(?:it|that|this|them)\s+)?up(?!\s+(?:at|to|from)\b)|check|find\s+out)(?:\s+(?:for|on|about|online|the\s+web|the\s+internet))?|(?:^\s*|(?<=[.!?,;:]\s*)|\b(?:please|pls|plz|ok|okay|anyway|hey|go)\s+)(?:please\s+)?(?:(?:search|google)(?:\s+(?:for|online|the\s+web|the\s+internet|it|that|this))?|look\s+(?:(?:it|that|this|them)\s+)?up(?!\s+(?:at|to|from)\b)|check\s+(?:online|the\s+web|the\s+internet)(?:\s+for)?)|\bwhat\s+does\s+(?:the\s+internet|google|the\s+web)\s+say(?:\s+about)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitRequest();

    // "Please don't search online for", "do not look it up", "never google this", "no googling", "without searching",
    // "keep this off the internet".
    [GeneratedRegex(@"\b(?:(?:do\s+not|don'?t|dont|never|not|no\s+need\s+to|stop|without|won'?t\s+you|shouldn'?t)\s+(?:\w+\s+){0,3}?(?:search|searching|google|googling|look|looking|check(?:ing)?\s+online|find\s+out\s+online)|no\s+(?:searching|googling|looking\s+(?:it\s+)?up)|(?:keep|leave)\s+(?:\w+\s+){0,3}?(?:off|out\s+of)\s+the\s+(?:internet|web))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeclinedSearch();

    // Words that show a question is about something recent or changing.
    [GeneratedRegex(@"\b(latest|newest|new\s+(?:version|release|update|season|album|game|movie|film|book|episode|trailer|model|phone)|just\s+(?:came\s+out|released|announced|dropped|launched)|came\s+out|coming\s+out|come\s+out|out\s+yet|release\s+date|released|announced|launched|this\s+(?:week|month|year)|last\s+(?:week|month)|right\s+now|currently|these\s+days|nowadays|news|recently|recent|upcoming|anymore|still\s+(?:alive|running|available|open)|who\s+won|score|standings|price\s+of|stock\s+price)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Recency();

    // Nouns that make a recency question about something public even without a name.
    [GeneratedRegex(@"\b(game|games|movie|movies|film|films|show|series|season|album|song|book|novel|phone|console|version|release|update|patch|election|news|headlines|team|match|tournament|championship|weather|forecast|price|prices|stock|market|launch|trailer|episode)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TopicNoun();

    [GeneratedRegex(@"^\s*(?:what|what's|whats|when|who|who's|which|where|why|how|is|are|was|were|did|does|do|has|have|had|any|anything|can|could|will|would|heard|know)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuestionStart();

    // Questions about the contact or the user themselves, which a search can never answer.
    [GeneratedRegex(@"\b(?:how\s+(?:are|have)\s+you|how's\s+it\s+going|what\s+(?:are|were)\s+you\s+(?:doing|up\s+to|thinking)|how\s+(?:was|is)\s+your|did\s+you\s+(?:sleep|eat|get\s+home|have\s+fun)|do\s+you\s+(?:remember|love|miss|like\s+me)|are\s+you\s+(?:ok|okay|there|free|busy|mad|sure|serious)|what\s+do\s+you\s+think\s+of\s+me|about\s+me)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AboutUs();

    // Capitalized words that do not name anything a search would help with.
    [GeneratedRegex(@"^(?:I|I'm|I'd|I'll|I've|Im|OK|Ok|Okay|Mom|Mum|Dad|Grandma|Grandpa|Nana|Papa|Grandad|Granddad|Auntie|Aunt|Uncle|Sis|Bro|Babe|Honey|Hon|Sweetie|God|Lol|LOL|Haha|Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|Mon|Tue|Wed|Thu|Fri|Sat|Sun|Christmas|Easter|Thanksgiving|Halloween|Today|Tomorrow|Yesterday|Tonight|Mr|Mrs|Ms|Dr|Miss)$", RegexOptions.CultureInvariant)]
    private static partial Regex CommonCapitalized();

    [GeneratedRegex(@"\b(?:19|20)\d{2}\b")]
    private static partial Regex Year();

    [GeneratedRegex(@"(?<=[.!?])\s+|[\r\n]+")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?:https?://|www\.)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex(@"\+?\(?\d[\d\s().-]{7,}\d")]
    private static partial Regex Phone();

    // Five or more digits in a row, such as an account, order, or house number; years have four.
    [GeneratedRegex(@"\b\d{5,}\b")]
    private static partial Regex LongNumber();

    // The request itself, so the query is the subject alone.
    [GeneratedRegex(@"\b(?:(?:can|could|would|will)\s+you\s+(?:please\s+)?(?:look\s+(?:it\s+|that\s+|this\s+)?up|search(?:\s+for)?|google|check(?:\s+online)?|find\s+out|tell\s+me)|look\s+(?:it|that|this|them)\s+up|look\s+up|search\s+(?:for|online|the\s+web|the\s+internet)|google\s+(?:it|that|this)|check\s+online|online|on\s+the\s+(?:web|internet)|do\s+you\s+know(?:\s+(?:if|whether|about|anything\s+about))?|have\s+you\s+(?:heard|seen|played|read|watched|tried)(?:\s+(?:of|about))?|what\s+do\s+you\s+think\s+(?:of|about)|what\s+does\s+(?:the\s+internet|google|the\s+web)\s+say(?:\s+about)?|tell\s+me\s+about|let\s+me\s+know|for\s+me|(?:can|could|would|will)\s+you(?:\s+please)?(?=[\s,?!.]*$))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RequestPhrase();

    [GeneratedRegex(@"\b(?:hey|hi|hello|yo|so|um|uh|hmm|please|pls|plz|lol|haha|hehe|omg|btw|actually|just|really|like|you|your|you're|i|i'm|im|me|my|mine|we|our|us)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Filler();

    // Anything but letters, digits, spaces, and the punctuation found in names and versions.
    [GeneratedRegex(@"[^\p{L}\p{N}\s\-.:'+#&]")]
    private static partial Regex Unsafe();
}
