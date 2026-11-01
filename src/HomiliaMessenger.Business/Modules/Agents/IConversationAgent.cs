using HomiliaMessenger.Business.Modules.Conversations;
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
public sealed record AgentRequest(string Model, string Instructions, IReadOnlyList<AgentTurn> Turns);

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
