using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.Profile;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HomiliaMessenger.Business.Modules.Agents;

/// <summary>
/// The rolling memory of a conversation: when the messages not yet in the contact's notes grow past
/// <see cref="FOLD_AFTER_CHARACTERS"/>, the contact's agent lists the facts worth remembering from all but the most recent
/// <see cref="KEEP_CHARACTERS"/>, and those facts are added to the notes. Agents are then given the notes plus the
/// messages after them.
/// </summary>
/// <remarks>
/// <para>
/// A notes-plus-recent-messages memory, as in LangChain's ConversationSummaryBufferMemory. Folding in large steps keeps
/// the prompt's start unchanged between folds, so local models can reuse their cache for most replies.
/// </para>
/// <para>
/// The notes are only ever appended to, never rewritten by a model: small models asked to rewrite a running summary drop
/// a little more of the earlier facts each time, while listing the facts in a few messages is well within their reach.
/// When the notes are full the oldest facts give way, and the user can always correct them.
/// </para>
/// </remarks>
internal static class MemoryNotes
{
    internal const int FOLD_AFTER_CHARACTERS = 2400;
    internal const int KEEP_CHARACTERS = 800;

    /// <summary>
    /// The index of the first message not yet condensed into the notes.
    /// </summary>
    internal static int FirstUnremembered(IReadOnlyList<ChatMessage> messages, MessageId? through)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (through is null)
        {
            return 0;
        }

        for (int index = 0; index < messages.Count; index++)
        {
            if (messages[index].Id == through)
            {
                return index + 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Where the notes should end after condensing, or <see langword="null"/> when the messages from
    /// <paramref name="start"/> still fit. The messages kept start with one from the user, so no reply is left between the
    /// notes and the question it answered. At most about <see cref="FOLD_AFTER_CHARACTERS"/> are condensed at once, so a
    /// long history (such as one from before the contact had a model) fits in what a model can read, and catches up a
    /// batch at a time.
    /// </summary>
    internal static int? FoldBoundary(IReadOnlyList<ChatMessage> messages, int start)
    {
        ArgumentNullException.ThrowIfNull(messages);

        long unremembered = messages.Skip(start).Sum(message => (long)message.Text.Length);

        if (unremembered <= FOLD_AFTER_CHARACTERS)
        {
            return null;
        }

        int boundary = messages.Count;
        long kept = 0;

        while (boundary > start && kept + messages[boundary - 1].Text.Length <= KEEP_CHARACTERS)
        {
            boundary--;
            kept += messages[boundary].Text.Length;
        }

        boundary = Math.Min(boundary, messages.Count - 1);

        // Start the kept messages at a message from the user: the next one if there is one, otherwise the one before, so
        // a long reply is never kept without the message it answered.
        int next = boundary;

        while (next < messages.Count && messages[next].Sender != MessageSender.User)
        {
            next++;
        }

        if (next < messages.Count)
        {
            boundary = next;
        }
        else
        {
            while (boundary > start && messages[boundary].Sender != MessageSender.User)
            {
                boundary--;
            }
        }

        if (boundary <= start)
        {
            return null;
        }

        // Limit the batch, ending it before a message from the user so each reply stays with the message it answered.
        int batchEnd = start;
        long batch = 0;

        while (batchEnd < boundary && (batchEnd == start || batch + messages[batchEnd].Text.Length <= FOLD_AFTER_CHARACTERS || messages[batchEnd].Sender != MessageSender.User))
        {
            batch += messages[batchEnd].Text.Length;
            batchEnd++;
        }

        return batchEnd;
    }

    internal const int MAX_FACTS_PER_FOLD = 3;

    /// <summary>
    /// Asks an agent to list the facts in <paramref name="folding"/> worth remembering that the notes do not have yet.
    /// </summary>
    /// <remarks>
    /// Only the user's messages are given. In testing with a small local model, including the contact's replies filled
    /// the few facts allowed per fold with the contact's small talk and crowded out what the user shared; with the user's
    /// messages alone, the facts that mattered were kept every time. The cost is that a plan the contact proposed is
    /// only remembered when the user's reply restates it, or the user adds it to the notes.
    /// </remarks>
    internal static AgentRequest ExtractRequest(Contact contact, UserProfile user, string? notes, IEnumerable<ChatMessage> folding, string model)
    {
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(folding);

        string name = contact.DisplayName;
        string userName = string.IsNullOrWhiteSpace(user.Name) ? "the user" : user.Name.Trim();

        string instructions = $"""
            You read messages {userName} sent to {name}, and list the lasting facts {name} should remember later.

            Lasting facts are things that will still matter weeks from now: people and pets and their names, places, work, important dates, life events, and plans or promises.
            Leave out passing remarks, such as the weather, meals, or how a day went, and anything already known.

            List at most {MAX_FACTS_PER_FOLD} facts, one short fact per line, starting with "- ". Write about {userName} in the third person, by name, never as "I".
            If there is nothing new worth remembering, reply with "- none".
            """;

        StringBuilder input = new();
        input.AppendLine("Already known:");
        input.AppendLine(string.IsNullOrWhiteSpace(notes) ? "- nothing yet" : notes.Trim());
        input.AppendLine();
        input.AppendLine($"Messages from {userName}:");

        foreach (ChatMessage message in folding.Where(message => message.Sender == MessageSender.User))
        {
            input.AppendLine($"- {message.Text.ReplaceLineEndings(" ")}");
        }

        return new AgentRequest(model, instructions, [new AgentTurn(MessageSender.User, input.ToString().TrimEnd())], Precise: true);
    }

    /// <summary>
    /// The facts in an agent's answer, one per line starting with "- ", without headings, commentary, or a "none"
    /// answer. Returns an empty list when nothing new was found.
    /// </summary>
    internal static IReadOnlyList<string> Facts(string? text)
    {
        List<string> facts = [];

        if (string.IsNullOrWhiteSpace(text))
        {
            return facts;
        }

        foreach (string raw in PersonaFraming.BeforeDivider(text).ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.Trim();

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("• ", StringComparison.Ordinal))
            {
                line = line[2..].Trim();
            }

            // Skip blank lines, headings such as "Facts:", commentary, and answers that there is nothing new.
            if (line.Length == 0 || line.EndsWith(':') || PersonaFraming.IsCommentary(line) || IsNothingNew(line))
            {
                continue;
            }

            facts.Add("- " + line);
        }

        return [.. facts.Take(MAX_FACTS_PER_FOLD)];
    }

    /// <summary>
    /// Appends <paramref name="facts"/> not already in <paramref name="notes"/>. When the result would exceed
    /// <see cref="Conversation.MAX_MEMORY_NOTES_LENGTH"/>, the oldest lines give way.
    /// </summary>
    internal static string? Append(string? notes, IReadOnlyList<string> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        List<string> lines = string.IsNullOrWhiteSpace(notes) ? [] : [.. notes.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)];

        foreach (string fact in facts)
        {
            if (!lines.Any(line => IsSameFact(line, fact)))
            {
                lines.Add(fact);
            }
        }

        while (lines.Count > 0 && lines.Sum(line => line.Length + 1) - 1 > Conversation.MAX_MEMORY_NOTES_LENGTH)
        {
            lines.RemoveAt(0);
        }

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>
    /// Whether <paramref name="fact"/> is <paramref name="known"/> again, differing only in case, punctuation, or spacing.
    /// </summary>
    /// <remarks>
    /// Deliberately strict. Any looser comparison of words treats some real changes as repeats, such as a corrected day
    /// ("Tuesday" after "Monday"), a reversed relationship ("Sam works for Aaron"), or a change of tense, and those
    /// corrections would be lost for good. A reworded repeat in the notes is the smaller harm, and the user can delete it.
    /// </remarks>
    internal static bool IsSameFact(string known, string fact)
    {
        return Normalized(known) == Normalized(fact);
    }

    private static string Normalized(string text)
    {
        StringBuilder normalized = new();

        foreach (char character in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                normalized.Append(character);
            }
            else if (normalized.Length > 0 && normalized[^1] != ' ')
            {
                normalized.Append(' ');
            }
        }

        return normalized.ToString().Trim();
    }

    private static bool IsNothingNew(string line)
    {
        string text = line.Trim().TrimEnd('.', '!').ToLowerInvariant();

        return text is "none" or "nothing" or "n/a" || text.StartsWith("nothing new", StringComparison.Ordinal) || text.StartsWith("no new", StringComparison.Ordinal);
    }
}
