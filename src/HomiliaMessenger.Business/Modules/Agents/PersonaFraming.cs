using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HomiliaMessenger.Business.Modules.Agents;

/// <summary>
/// Composes what an agent is asked: instructions to answer in character as the contact, and the recent conversation as
/// memory. Also tidies what comes back, since small local models sometimes add commentary about their own reply.
/// </summary>
internal static partial class PersonaFraming
{
    /// <summary>
    /// Memory is the most recent messages within this many characters. Small models on a CPU are slow to read long
    /// prompts and have small context windows, so the budget stays modest.
    /// </summary>
    internal const int MAX_MEMORY_CHARACTERS = 3500;

    /// <summary>
    /// Memory may only start at the first message at or after each multiple of this many characters into the
    /// conversation. Those boundaries never move as messages are added, so the memory's first message stays the same for
    /// several replies instead of shifting with every message, and providers can reuse their work on the unchanged start
    /// of the prompt. At most this much of the budget goes unused.
    /// </summary>
    internal const int MEMORY_STEP_CHARACTERS = MAX_MEMORY_CHARACTERS / 4;

    internal static AgentRequest Build(Contact contact, IReadOnlyList<ChatMessage> messages, string model, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(messages);

        return new AgentRequest(model, Instructions(contact, today), Memory(messages));
    }

    internal static string Instructions(Contact contact, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(contact);

        string name = contact.DisplayName;
        StringBuilder builder = new();

        builder.AppendLine($"You are {name}, a real person texting with someone you know in a messaging app. You are not an AI, a language model, or an assistant, and you never say or suggest that you are.");
        builder.AppendLine();
        builder.AppendLine("About you:");

        string fullName = string.Join(" ", new[] { contact.GivenName, contact.FamilyName }.Where(value => !string.IsNullOrWhiteSpace(value)));

        if (fullName.Length > 0 && fullName != name)
        {
            builder.AppendLine($"- Full name: {fullName}");
        }

        if (contact.Birthday is { } birthday)
        {
            string born = birthday.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
            builder.AppendLine($"- Age: {contact.AgeOn(today)} (born {born})");
        }

        AppendField(builder, "Gender", contact.Gender);
        AppendField(builder, "Personality", contact.Personality);
        AppendField(builder, "Background", contact.Background);

        builder.AppendLine();
        builder.AppendLine($"Today is {today.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)}.");
        builder.AppendLine();
        builder.AppendLine("How to reply:");
        builder.AppendLine($"- Write only {name}'s next text message, the way {name} would actually text: casual and natural, usually one to three short sentences.");
        builder.AppendLine("- Draw on your background and personality when it fits, including what you would rather not talk about.");
        builder.Append("- Do not add notes, explanations, narration, or commentary about your reply.");

        return builder.ToString();
    }

    /// <summary>
    /// The most recent messages within the memory budget, oldest first, always including the latest message. Consecutive
    /// messages from the same sender are joined into one turn and the memory starts with a user turn, since some models'
    /// chat formats require turns to alternate starting with the user.
    /// </summary>
    internal static IReadOnlyList<AgentTurn> Memory(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        long total = messages.Sum(message => (long)message.Text.Length);
        long offset = 0;
        long nextBoundary = 0;
        int start = Math.Max(messages.Count - 1, 0);

        for (int index = 0; index < messages.Count - 1; index++)
        {
            if (offset >= nextBoundary)
            {
                if (total - offset <= MAX_MEMORY_CHARACTERS)
                {
                    start = index;
                    break;
                }

                nextBoundary = ((offset / MEMORY_STEP_CHARACTERS) + 1) * MEMORY_STEP_CHARACTERS;
            }

            offset += messages[index].Text.Length;
        }

        List<AgentTurn> turns = [];

        foreach (ChatMessage message in messages.Skip(start).SkipWhile(message => message.Sender != MessageSender.User))
        {
            if (turns.Count > 0 && turns[^1].Sender == message.Sender)
            {
                turns[^1] = turns[^1] with { Text = turns[^1].Text + "\n" + message.Text };
            }
            else
            {
                turns.Add(new AgentTurn(message.Sender, message.Text));
            }
        }

        return turns;
    }

    /// <summary>
    /// Removes what is not part of the persona's message: a leading "Name:" label, surrounding quotes, and trailing
    /// paragraphs of commentary such as "(Note: ...)". Returns <see langword="null"/> when nothing is left.
    /// </summary>
    internal static string? CleanReply(string? text, Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string cleaned = text.Trim();

        foreach (string? label in new[] { contact.DisplayName, contact.Nickname, contact.GivenName })
        {
            if (!string.IsNullOrWhiteSpace(label) && cleaned.StartsWith(label + ":", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[(label.Length + 1)..].TrimStart();
                break;
            }
        }

        // Models sometimes follow the reply with a divider or heading and an explanation of it.
        cleaned = Divider().Split(cleaned)[0];

        IEnumerable<string> paragraphs = ParagraphBreak().Split(cleaned)
            .TakeWhile(paragraph => !Commentary().IsMatch(paragraph));
        cleaned = string.Join("\n\n", paragraphs).Trim();

        if (cleaned.Length > 1 && cleaned[0] == '"' && cleaned[^1] == '"' && cleaned.Count(character => character == '"') == 2)
        {
            cleaned = cleaned[1..^1].Trim();
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    private static void AppendField(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.AppendLine($"- {label}: {value.Trim()}");
        }
    }

    [GeneratedRegex(@"\r?\n\s*\r?\n")]
    private static partial Regex ParagraphBreak();

    // Paragraphs such as "(Note: ...)", "*Note*: ...", "[This response ...]", "In this reply, ...", or "Explanation: ...".
    [GeneratedRegex(@"^\s*[\(\[\*_]*\s*(note|(in )?(this|the above) (response|reply|dialogue|message|conversation)|explanation|commentary)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Commentary();

    // A line of three or more dashes, asterisks, or underscores, or a Markdown heading such as "#### End of Reply".
    [GeneratedRegex(@"^\s*(-{3,}|\*{3,}|_{3,}|\#{1,6}\s.*)\s*$", RegexOptions.Multiline)]
    private static partial Regex Divider();
}
