using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Profile;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HomiliaMessenger.Business.Modules.Conversations;

/// <summary>
/// The one question a contact's model is asked to help coordinate a group: would this contact add a message now? Kept
/// to a yes or no, since local models cannot be relied on for tool calls or structured output, and anything else is
/// treated as no answer.
/// </summary>
internal static class GroupDecisions
{
    /// <summary>
    /// The last few messages are enough to judge whether there is something to add.
    /// </summary>
    internal const int RECENT_MESSAGES = 6;

    internal static AgentRequest WouldSpeakRequest(Conversation conversation, Contact candidate, IReadOnlyList<Contact> contacts, UserProfile user, IReadOnlyList<ChatMessage> messages, string model)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(messages);

        string name = candidate.DisplayName;
        string userName = string.IsNullOrWhiteSpace(user.Name) ? "Me" : user.Name.Trim();
        Dictionary<ContactId, string> names = contacts.ToDictionary(contact => contact.Id, contact => contact.DisplayName);
        StringBuilder instructions = new();

        instructions.AppendLine($"You decide whether {name} would naturally send a message in a group chat right now.");

        if (!string.IsNullOrWhiteSpace(candidate.Personality))
        {
            instructions.AppendLine($"{name}'s personality: {candidate.Personality.ReplaceLineEndings(" ").Trim()}");
        }

        instructions.AppendLine($"Answer YES if {name} has something of their own to add, such as a reaction, a different view, or a question. Answer NO if the conversation is fine without them, or they would only repeat what was said.");
        instructions.Append("Reply with only YES or NO.");

        StringBuilder chat = new();

        foreach (ChatMessage message in messages.TakeLast(RECENT_MESSAGES))
        {
            string author = conversation.SenderOf(message) is { } id ? names.GetValueOrDefault(id, "Someone") : userName;
            chat.AppendLine($"{author}: {message.Text.ReplaceLineEndings(" ")}");
        }

        chat.Append($"Would {name} send a message now?");

        return new AgentRequest(model, instructions.ToString(), [new AgentTurn(MessageSender.User, chat.ToString())], ["\n", ".", ","], Precise: true);
    }

    /// <summary>
    /// <see langword="true"/> for an answer starting "yes", <see langword="false"/> for one starting "no", and
    /// <see langword="null"/> for anything else.
    /// </summary>
    internal static bool? ReadYesNo(string? answer)
    {
        string word = new([.. (answer ?? string.Empty).Trim().TrimStart('*', '"', '\'', '(', '[').TakeWhile(char.IsLetter)]);

        return word.ToUpperInvariant() switch
        {
            "YES" => true,
            "NO" => false,
            _ => null
        };
    }
}
