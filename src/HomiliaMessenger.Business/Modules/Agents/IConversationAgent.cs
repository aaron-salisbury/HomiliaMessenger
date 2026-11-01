using HomiliaMessenger.Business.Modules.Conversations;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Agents;

/// <summary>
/// One earlier message given to an agent as conversation memory.
/// </summary>
public sealed record AgentTurn(MessageSender Sender, string Text);

/// <summary>
/// What an agent is asked to answer: the persona instructions composed by Business, and the conversation so far, oldest
/// first, ending with the user's latest message.
/// </summary>
/// <param name="StopTexts">
/// Text that ends the answer wherever it appears after the start, such as a speaker label like "User:" that shows a model
/// has begun writing the next turn itself. Providers stop generating there.
/// </param>
/// <param name="Precise">
/// Asks for the model's most likely answer rather than a varied one, for tasks such as listing facts, where the same
/// input should give the same result.
/// </param>
public sealed record AgentRequest(string Model, string Instructions, IReadOnlyList<AgentTurn> Turns, IReadOnlyList<string>? StopTexts = null, bool Precise = false)
{
    /// <summary>
    /// The index where the answer should end: the first stop text after the answer's first character, or the whole text.
    /// Leading whitespace is skipped, so an answer that opens with a speaker label, such as "\nWalt: Hi", is not cut off
    /// before it starts.
    /// </summary>
    public int EndOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int end = text.Length;
        int start = 0;

        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        // A stop text only counts after the first character, so there is nothing to find in text shorter than that.
        if (start + 1 >= text.Length)
        {
            return end;
        }

        foreach (string stop in StopTexts ?? [])
        {
            int index = text.IndexOf(stop, start + 1, StringComparison.OrdinalIgnoreCase);

            if (index > start && index < end)
            {
                end = index;
            }
        }

        return end;
    }
}

/// <summary>
/// Expected outcomes of asking an agent, or a contact, to reply.
/// </summary>
public enum ReplyStatus
{
    Success,

    /// <summary>
    /// The conversation no longer exists.
    /// </summary>
    NotFound,

    /// <summary>
    /// The agent cannot answer right now, for example because its model has not been downloaded.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The agent answered with nothing usable.
    /// </summary>
    InvalidResponse
}

/// <summary>
/// An agent's answer. <see cref="Text"/> is set on success; <see cref="Problem"/> explains any other status.
/// </summary>
public sealed record AgentReply(ReplyStatus Status, string? Text, string? Problem)
{
    public static AgentReply Success(string text)
    {
        return new(ReplyStatus.Success, text, null);
    }

    public static AgentReply Unavailable(string problem)
    {
        return new(ReplyStatus.Unavailable, null, problem);
    }

    public static AgentReply InvalidResponse(string problem)
    {
        return new(ReplyStatus.InvalidResponse, null, problem);
    }
}

/// <summary>
/// Capability to answer as a persona. A provider carries the request to a model and returns its text; Business decides
/// who the persona is.
/// </summary>
public interface IConversationAgent
{
    /// <summary>
    /// The provider name contacts are assigned to, matched case-insensitively against <c>AgentAssignment.Provider</c>.
    /// </summary>
    string Provider { get; }

    Task<AgentReply> ReplyAsync(AgentRequest request, CancellationToken cancellationToken = default);
}
