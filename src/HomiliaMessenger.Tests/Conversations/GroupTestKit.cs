using FluentValidation.Results;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Conversations;

/// <summary>
/// Shared setup for group tests: a database with deterministic turn-taking, and an agent whose answers are scripted.
/// </summary>
internal static class GroupTestKit
{
    internal const string PROVIDER = "Scripted";

    /// <summary>
    /// A database whose group coordinator draws from a seeded random source, whose simulated contacts reply after
    /// <paramref name="replyDelay"/>, and that also offers any <paramref name="agents"/>.
    /// </summary>
    internal static Task<TestDatabase> CreateDatabaseAsync(TestContext testContext, TimeSpan replyDelay = default, GroupCoordinatorOptions? options = null, int seed = 3, params IConversationAgent[] agents)
    {
        return TestDatabase.CreateAsync(testContext, services =>
        {
            services.AddSingleton<IConversationAgent>(new SimulatedAgent(new Random(7), TimeProvider.System, replyDelay, replyDelay));
            services.AddSingleton(new GroupCoordinator(options ?? new GroupCoordinatorOptions(), new Random(seed)));

            foreach (IConversationAgent agent in agents)
            {
                services.AddSingleton(agent);
            }
        });
    }

    internal static async Task<Contact> AddContactAsync(TestDatabase database, string name, TestContext testContext, string? model = null, string? personality = null, string? voice = null, string? relationship = null)
    {
        Contact contact = new()
        {
            Id = ContactId.New(),
            GivenName = name,
            Birthday = new DateOnly(1980, 1, 1),
            Agent = model is null ? null : new AgentAssignment(PROVIDER, model),
            Personality = personality,
            VoiceId = voice,
            Relationship = relationship
        };
        ValidationResult result = await database.Services.GetRequiredService<IContactService>().SaveAsync(contact, testContext.CancellationToken);
        Assert.IsTrue(result.IsValid, result.ToString());

        return contact;
    }

    internal static async Task<Conversation> CreateGroupAsync(TestDatabase database, TestContext testContext, params Contact[] contacts)
    {
        GroupCreationResult result = await database.Services.GetRequiredService<IConversationService>().CreateGroupAsync([.. contacts.Select(contact => contact.Id)], testContext.CancellationToken);
        Assert.IsTrue(result.Validation.IsValid, result.Validation.ToString());

        return result.Group!;
    }

    /// <summary>
    /// Runs a round of turns as the messenger does, returning the replies in order.
    /// </summary>
    internal static async Task<IReadOnlyList<ChatMessage>> RunRoundAsync(IConversationService service, ConversationId group, GroupTrigger trigger, CancellationToken cancellationToken)
    {
        List<ChatMessage> replies = [];
        GroupCycle cycle = new(trigger);

        // Far more than any round may take, so a runaway round fails the test rather than hanging it.
        for (int guard = 0; guard < 20; guard++)
        {
            if (await service.PlanGroupTurnAsync(group, cycle, cancellationToken) is not { } turn)
            {
                return replies;
            }

            ConversationReply reply = await service.ReplyInGroupAsync(group, turn, cancellationToken: cancellationToken);
            Assert.AreEqual(ReplyStatus.Success, reply.Status, reply.Problem);
            replies.Add(reply.Message!);
            cycle = cycle.After(turn);
        }

        Assert.Fail("The round did not end.");
        return replies;
    }
}

/// <summary>
/// An agent for every scripted contact: records what it is asked, and answers as <see cref="Answer"/> says, by default
/// "&lt;name&gt; here." in the asking persona's name, "NO" when asked whether to speak, and "- none" when listing facts.
/// </summary>
internal sealed class ScriptedAgent : IConversationAgent
{
    private readonly Lock _lock = new();
    private readonly List<AgentRequest> _requests = [];

    public string Provider => GroupTestKit.PROVIDER;

    public TimeSpan Delay { get; set; }

    public Func<AgentRequest, AgentReply?>? Answer { get; set; }

    public IReadOnlyList<AgentRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>
    /// The replies asked for (not decisions or fact lists), in order.
    /// </summary>
    public IReadOnlyList<AgentRequest> ReplyRequests => [.. Requests.Where(request => !request.Precise)];

    public IReadOnlyList<AgentRequest> DecisionRequests => [.. Requests.Where(IsDecision)];

    public async Task<AgentReply> ReplyAsync(AgentRequest request, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _requests.Add(request);
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (Answer?.Invoke(request) is { } scripted)
        {
            return scripted;
        }

        if (IsDecision(request))
        {
            return AgentReply.Success("NO");
        }

        return request.Precise ? AgentReply.Success("- none") : AgentReply.Success($"{SpeakerOf(request)} here.");
    }

    internal static bool IsDecision(AgentRequest request)
    {
        return request.Instructions.StartsWith("You decide whether", StringComparison.Ordinal);
    }

    /// <summary>
    /// The persona a reply request is for, from its opening "You are &lt;name&gt;, ...".
    /// </summary>
    internal static string SpeakerOf(AgentRequest request)
    {
        const string opening = "You are ";

        return request.Instructions.StartsWith(opening, StringComparison.Ordinal)
            ? request.Instructions[opening.Length..request.Instructions.IndexOf(',', opening.Length)]
            : "Someone";
    }
}
