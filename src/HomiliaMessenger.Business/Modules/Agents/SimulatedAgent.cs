using HomiliaMessenger.Business.Modules.Contacts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.Agents;

/// <summary>
/// Stands in for a real agent: replies with random canned text after a short, human-feeling pause. Contacts without an
/// assigned agent use it, so the full messaging flow works without any model.
/// </summary>
internal sealed class SimulatedAgent : IConversationAgent
{
    internal const string PROVIDER = "Simulated";

    /// <summary>
    /// Recorded on replies from contacts without an assigned agent.
    /// </summary>
    internal static readonly AgentAssignment ASSIGNMENT = new(PROVIDER, "canned");

    private static readonly string[] CANNED_REPLIES =
    [
        "Ha, I was just thinking about that.",
        "Oh really? Tell me more.",
        "Hmm, I'm not sure how I feel about that.",
        "That reminds me of something that happened years ago.",
        "Sorry, I was making coffee. What were we saying?",
        "You always know how to make me laugh.",
        "Honestly? I'd rather not get into that today.",
        "Well, that's one way to look at it.",
        "I've been meaning to ask you the same thing.",
        "Now that's a story. How did it turn out?",
        "Let me think on that and get back to you.",
        "You're not wrong.",
        "Ok, but have you tried just sleeping on it?",
        "I can't believe it's already been that long.",
        "That sounds about right, knowing you.",
        "Funny you should mention it.",
        "I hear you. It's been a long week here too.",
        "Wait, start from the beginning.",
        "Sure, why not?",
        "Good to hear from you, by the way."
    ];

    private static readonly TimeSpan DEFAULT_MIN_DELAY = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan DEFAULT_MAX_DELAY = TimeSpan.FromSeconds(3.5);

    private readonly Random _random;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _minDelay;
    private readonly TimeSpan _maxDelay;
    private readonly Lock _lock = new();
    private int _lastIndex = -1;

    public SimulatedAgent() : this(Random.Shared, TimeProvider.System, DEFAULT_MIN_DELAY, DEFAULT_MAX_DELAY)
    {
    }

    internal SimulatedAgent(Random random, TimeProvider timeProvider, TimeSpan minDelay, TimeSpan maxDelay)
    {
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentOutOfRangeException.ThrowIfLessThan(minDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelay, minDelay);
        _minDelay = minDelay;
        _maxDelay = maxDelay;
    }

    internal static IReadOnlyList<string> Replies => CANNED_REPLIES;

    public string Provider => PROVIDER;

    public async Task<AgentReply> ReplyAsync(AgentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return AgentReply.Success(await NextAsync(cancellationToken));
    }

    /// <summary>
    /// Waits as if the contact were typing, then returns a canned reply that differs from the previous one.
    /// </summary>
    internal async Task<string> NextAsync(CancellationToken cancellationToken = default)
    {
        string reply;
        TimeSpan delay;

        lock (_lock)
        {
            int index = _random.Next(CANNED_REPLIES.Length - 1);

            if (index >= _lastIndex && _lastIndex >= 0)
            {
                index++;
            }

            _lastIndex = index;
            reply = CANNED_REPLIES[index];
            delay = _minDelay + ((_maxDelay - _minDelay) * _random.NextDouble());
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _timeProvider, cancellationToken);
        }

        return reply;
    }
}
