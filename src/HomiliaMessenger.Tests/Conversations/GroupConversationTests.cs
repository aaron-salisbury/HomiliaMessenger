using Dapper;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.Memories;
using HomiliaMessenger.Business.Modules.Profile;
using HomiliaMessenger.Data.Database;
using HomiliaMessenger.Data.Database.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Conversations;

[TestClass]
public sealed class GroupConversationTests
{
    private const string ATLAS = "Aaron is starting a new project at work called Atlas.";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CreateGroup_FromADirectChat_LeavesTheChatAndItsMemoriesAsTheyWere()
    {
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        IMemoryService memories = database.Services.GetRequiredService<IMemoryService>();
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext);
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext);
        SendMessageResult sent = await service.SendAsync(marcus.Id, "Started something new at work.", TestContext.CancellationToken);
        await memories.AddAsync(sent.Conversation!.Id, ATLAS, TestContext.CancellationToken);

        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, elena);
        Conversation? direct = await service.FindByContactAsync(marcus.Id, TestContext.CancellationToken);
        IReadOnlyList<ChatMessage> directMessages = await service.ListMessagesAsync(direct!.Id, TestContext.CancellationToken);
        IReadOnlyList<ChatMessage> groupMessages = await service.ListMessagesAsync(group.Id, TestContext.CancellationToken);

        Assert.AreEqual(sent.Conversation.Id, direct.Id);
        Assert.AreNotEqual(direct.Id, group.Id);
        Assert.AreEqual(ConversationKind.Direct, direct.Kind);
        Assert.HasCount(1, directMessages);
        Assert.IsEmpty(groupMessages);
        Assert.AreSequenceEqual(new[] { marcus.Id, elena.Id }, group.ContactIds.ToArray());
        Assert.AreEqual(GroupInitiation.Occasional, group.Initiation);
        Assert.AreSequenceEqual(new[] { ATLAS }, (await memories.ListAsync(marcus.Id, TestContext.CancellationToken)).Select(memory => memory.Text).ToArray());
        Assert.HasCount(2, await service.ListAsync(TestContext.CancellationToken));
        Assert.AreEqual("Marcus & Elena", GroupConversations.DisplayName(group, [marcus, elena]));
    }

    [TestMethod]
    public async Task CreateGroup_AllowsExactlyTwoContactsAndReusesAnExistingGroup()
    {
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext);
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext);
        Contact jo = await GroupTestKit.AddContactAsync(database, "Jo", TestContext);

        GroupCreationResult alone = await service.CreateGroupAsync([marcus.Id, marcus.Id], TestContext.CancellationToken);
        GroupCreationResult crowd = await service.CreateGroupAsync([marcus.Id, elena.Id, jo.Id], TestContext.CancellationToken);
        GroupCreationResult missing = await service.CreateGroupAsync([marcus.Id, ContactId.New()], TestContext.CancellationToken);
        GroupCreationResult first = await service.CreateGroupAsync([marcus.Id, elena.Id], TestContext.CancellationToken);
        GroupCreationResult again = await service.CreateGroupAsync([elena.Id, marcus.Id], TestContext.CancellationToken);

        Assert.IsFalse(alone.Validation.IsValid);
        Assert.IsFalse(crowd.Validation.IsValid);
        Assert.IsFalse(missing.Validation.IsValid);
        Assert.IsTrue(first.Validation.IsValid);
        Assert.IsFalse(first.IsExisting);
        Assert.IsTrue(again.IsExisting);
        Assert.AreEqual(first.Group!.Id, again.Group!.Id);
        Assert.HasCount(1, await service.ListAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task CreateGroup_StartedTwiceAtOnce_GivesOneGroup()
    {
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext);
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext);

        GroupCreationResult[] results = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => Task.Run(() =>
            service.CreateGroupAsync(index % 2 == 0 ? [marcus.Id, elena.Id] : [elena.Id, marcus.Id], TestContext.CancellationToken))));

        Assert.IsTrue(results.All(result => result.Validation.IsValid));
        Assert.HasCount(1, results.Select(result => result.Group!.Id).Distinct());
        Assert.HasCount(1, results.Where(result => !result.IsExisting));
        Assert.HasCount(1, await service.ListAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task GroupMessages_AreAttributedKeptInOrderAndSurviveARestart()
    {
        ScriptedAgent agent = new();
        TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, agents: agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext, model: "model-a");
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext, model: "model-b");
        await service.SendAsync(marcus.Id, "Private hello", TestContext.CancellationToken);
        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, elena);

        await service.SendAsync(group.Id, "Marcus, how was the shop?", TestContext.CancellationToken);
        ConversationReply fromMarcus = await service.ReplyInGroupAsync(group.Id, new GroupTurn(marcus.Id, GroupTurnReason.Addressed), cancellationToken: TestContext.CancellationToken);
        ConversationReply fromElena = await service.ReplyInGroupAsync(group.Id, new GroupTurn(elena.Id, GroupTurnReason.FollowingUp), cancellationToken: TestContext.CancellationToken);
        ConversationReply outsider = await service.ReplyInGroupAsync(group.Id, new GroupTurn(ContactId.New(), GroupTurnReason.FollowingUp), cancellationToken: TestContext.CancellationToken);

        using TestDatabase reopened = await database.ReopenAsync(TestContext, services => services.AddSingleton<IConversationAgent>(agent));
        IConversationService after = reopened.Services.GetRequiredService<IConversationService>();
        Conversation? stored = await after.GetAsync(group.Id, TestContext.CancellationToken);
        IReadOnlyList<ChatMessage> messages = await after.ListMessagesAsync(group.Id, TestContext.CancellationToken);
        IReadOnlyList<ChatMessage> direct = await after.ListMessagesAsync((await after.FindByContactAsync(marcus.Id, TestContext.CancellationToken))!.Id, TestContext.CancellationToken);

        Assert.AreEqual(ReplyStatus.Success, fromMarcus.Status);
        Assert.AreEqual(ReplyStatus.Success, fromElena.Status);
        Assert.AreEqual(ReplyStatus.NotFound, outsider.Status);
        Assert.IsNotNull(stored);
        Assert.IsTrue(stored.IsGroup);
        Assert.AreSequenceEqual(new[] { marcus.Id, elena.Id }, stored.ContactIds.ToArray());
        Assert.AreSequenceEqual(new ContactId?[] { null, marcus.Id, elena.Id }, messages.Select(stored.SenderOf).ToArray());
        Assert.AreSequenceEqual(new[] { "Marcus, how was the shop?", "Marcus here.", "Elena here." }, messages.Select(message => message.Text).ToArray());
        Assert.AreEqual("model-a", messages[1].Agent!.Model);
        Assert.AreEqual("model-b", messages[2].Agent!.Model);
        Assert.AreSequenceEqual(new[] { "Private hello" }, direct.Select(message => message.Text).ToArray());
        Assert.AreEqual(messages[^1].Id, stored.LastMessage!.Id);
    }

    [TestMethod]
    public async Task Store_RefusesAMessageFromAContactOutsideTheConversation()
    {
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext);
        IConversationStore store = database.Services.GetRequiredService<IConversationStore>();
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext);
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext);
        Contact jo = await GroupTestKit.AddContactAsync(database, "Jo", TestContext);
        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, elena);

        bool added = await store.AddMessageAsync(new ChatMessage { Id = MessageId.New(), ConversationId = group.Id, Sender = MessageSender.Contact, SenderContactId = jo.Id, Text = "Hi", SentAtUtc = DateTimeOffset.UtcNow }, TestContext.CancellationToken);

        Assert.IsFalse(added);
    }

    [TestMethod]
    public async Task EachContact_RepliesWithTheirOwnPersonaModelAndMemoriesOnly()
    {
        ScriptedAgent agent = new();
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, agents: agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        await database.Services.GetRequiredService<IUserProfileService>().SaveAsync(new UserProfile { Name = "Aaron" }, TestContext.CancellationToken);
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext, model: "model-a", personality: "Dry humor, fixes old motorcycles.", relationship: "Coworker");
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext, model: "model-b", personality: "Warm, teaches high school chemistry.", relationship: "Sister");
        SendMessageResult privately = await service.SendAsync(marcus.Id, "Big news at work.", TestContext.CancellationToken);
        await database.Services.GetRequiredService<IMemoryService>().AddAsync(privately.Conversation!.Id, ATLAS, TestContext.CancellationToken);
        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, elena);

        await service.SendAsync(group.Id, "Made progress on it today, Marcus!", TestContext.CancellationToken);
        GroupTurn? turn = await service.PlanGroupTurnAsync(group.Id, new GroupCycle(GroupTrigger.UserMessage), TestContext.CancellationToken);
        await service.ReplyInGroupAsync(group.Id, turn!, cancellationToken: TestContext.CancellationToken);
        await service.ReplyInGroupAsync(group.Id, new GroupTurn(elena.Id, GroupTurnReason.FollowingUp), cancellationToken: TestContext.CancellationToken);

        AgentRequest toMarcus = agent.ReplyRequests[0];
        AgentRequest toElena = agent.ReplyRequests[1];

        Assert.AreEqual(marcus.Id, turn!.Speaker);
        Assert.AreEqual(GroupTurnReason.Addressed, turn.Reason);
        Assert.AreEqual("model-a", toMarcus.Model);
        Assert.AreEqual("model-b", toElena.Model);
        Assert.StartsWith("You are Marcus, a real person in a group chat with Aaron and Elena", toMarcus.Instructions);
        Assert.Contains("Dry humor", toMarcus.Instructions);
        Assert.DoesNotContain("chemistry", toMarcus.Instructions);
        Assert.Contains("chemistry", toElena.Instructions);
        Assert.DoesNotContain("motorcycles", toElena.Instructions);
        Assert.Contains("- Elena, Aaron's sister", toMarcus.Instructions);

        // Marcus remembers what he was told privately, and is asked not to volunteer it; Elena was never told.
        Assert.Contains(ATLAS, toMarcus.Instructions);
        Assert.Contains("don't volunteer private or sensitive details", toMarcus.Instructions);
        Assert.DoesNotContain("Atlas", toElena.Instructions);
        Assert.DoesNotContain(turn => turn.Text.Contains("Atlas", StringComparison.Ordinal), toElena.Turns);
        Assert.DoesNotContain(turn => turn.Text.Contains("Big news at work", StringComparison.Ordinal), toElena.Turns, "A private chat is never given to another contact.");

        // Each speaker sees who said what, and is stopped from writing for anyone else.
        Assert.AreEqual(new AgentTurn(MessageSender.User, "Aaron: Made progress on it today, Marcus!\nMarcus: Marcus here.\n[Elena's turn to write in the group chat. Add something of your own to what was just said: a reaction, a different view, a joke, or a question. You can disagree if you would.]"), toElena.Turns[^1]);
        Assert.EndsWith("[Marcus's turn to write in the group chat. You were just spoken to by name, so answer that.]", toMarcus.Turns[^1].Text);
        Assert.Contains("Marcus:", toElena.StopTexts!);
        Assert.Contains("Aaron:", toElena.StopTexts!);
    }

    [TestMethod]
    public void CleanGroupReply_RejectsAReplyWrittenAsSomeoneElse()
    {
        Contact marcus = new() { Id = ContactId.New(), GivenName = "Marcus" };
        Contact elena = new() { Id = ContactId.New(), GivenName = "Elena" };
        UserProfile user = new() { Name = "Aaron" };

        Assert.IsNull(PersonaFraming.CleanGroupReply("Elena: I agree!", marcus, user, [elena]));
        Assert.IsNull(PersonaFraming.CleanGroupReply("Aaron: thanks", marcus, user, [elena]));
        Assert.IsNull(PersonaFraming.CleanGroupReply("[Marcus's turn to write in the group chat]", marcus, user, [elena]));
        Assert.AreEqual("Sounds good.", PersonaFraming.CleanGroupReply("Marcus: Sounds good.", marcus, user, [elena]));
    }

    [TestMethod]
    public async Task OpeningAGroup_UsesUpItsChanceToStartAConversation()
    {
        ScriptedAgent agent = new();
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, agents: agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext, model: "m");
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext, model: "m");
        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, elena);
        Contact jo = await GroupTestKit.AddContactAsync(database, "Jo", TestContext, model: "m");
        Conversation quiet = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, jo);
        await service.SetInitiationAsync(quiet.Id, GroupInitiation.Off, TestContext.CancellationToken);

        // A hello that was never written (the user left, or the model failed) is tried again next time.
        GroupTurn? interrupted = await service.PlanGroupTurnAsync(group.Id, new GroupCycle(GroupTrigger.Opened), TestContext.CancellationToken);
        GroupTurn? greeting = await service.PlanGroupTurnAsync(group.Id, new GroupCycle(GroupTrigger.Opened), TestContext.CancellationToken);
        await service.ReplyInGroupAsync(group.Id, greeting!, cancellationToken: TestContext.CancellationToken);
        GroupTurn? reopened = await service.PlanGroupTurnAsync(group.Id, new GroupCycle(GroupTrigger.Opened), TestContext.CancellationToken);
        GroupTurn? off = await service.PlanGroupTurnAsync(quiet.Id, new GroupCycle(GroupTrigger.Opened), TestContext.CancellationToken);
        Conversation? stored = await service.GetAsync(group.Id, TestContext.CancellationToken);
        Conversation? storedQuiet = await service.GetAsync(quiet.Id, TestContext.CancellationToken);

        Assert.AreEqual(elena.Id, interrupted!.Speaker);
        Assert.AreEqual(elena.Id, greeting!.Speaker);
        Assert.AreEqual(GroupTurnReason.Greeting, greeting.Reason);
        Assert.IsNull(reopened, "Opening again within the cooldown brings nothing.");
        Assert.IsNull(off);
        Assert.IsNotNull(stored!.LastInitiationAtUtc);
        Assert.IsNull(storedQuiet!.LastInitiationAtUtc);
        Assert.AreEqual(GroupInitiation.Off, storedQuiet.Initiation);
    }

    [TestMethod]
    public async Task AGroupOpenedTwiceAtOnce_IsGreetedOnce()
    {
        ScriptedAgent agent = new() { Delay = TimeSpan.FromMilliseconds(100) };
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, agents: agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext, model: "m");
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext, model: "m");
        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, elena);

        GroupTurn?[] turns = await Task.WhenAll(
            service.PlanGroupTurnAsync(group.Id, new GroupCycle(GroupTrigger.Opened), TestContext.CancellationToken),
            service.PlanGroupTurnAsync(group.Id, new GroupCycle(GroupTrigger.Opened), TestContext.CancellationToken));
        ConversationReply[] replies = await Task.WhenAll(turns.Select(turn => service.ReplyInGroupAsync(group.Id, turn!, cancellationToken: TestContext.CancellationToken)));

        Assert.IsTrue(turns.All(turn => turn?.Reason == GroupTurnReason.Greeting));
        Assert.HasCount(1, replies.Where(reply => reply.Status == ReplyStatus.Success));
        Assert.HasCount(1, await service.ListMessagesAsync(group.Id, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ARound_IsBoundedWhenContactsKeepTalkingToEachOther()
    {
        ScriptedAgent agent = new();
        agent.Answer = request => request.Precise ? null : AgentReply.Success(ScriptedAgent.SpeakerOf(request) == "Marcus" ? "Elena, you're wrong." : "No, Marcus, you are.");
        GroupCoordinatorOptions options = new() { MaxAutomaticTurns = 3 };
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, options: options, agents: agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext, model: "a");
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext, model: "b");
        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, elena);

        await service.SendAsync(group.Id, "Marcus, pineapple on pizza?", TestContext.CancellationToken);
        IReadOnlyList<ChatMessage> replies = await GroupTestKit.RunRoundAsync(service, group.Id, GroupTrigger.UserMessage, TestContext.CancellationToken);

        Assert.HasCount(3, replies);
        Assert.AreSequenceEqual(new ContactId?[] { marcus.Id, elena.Id, marcus.Id }, replies.Select(reply => reply.SenderContactId).ToArray());
    }

    [TestMethod]
    public async Task AContactWhoCannotReply_LeavesTheOtherToAnswer()
    {
        ScriptedAgent agent = new();
        agent.Answer = request => !request.Precise && ScriptedAgent.SpeakerOf(request) == "Marcus" ? AgentReply.Unavailable("Marcus's model has not been downloaded yet.") : null;
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, agents: agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext, model: "missing");
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext, model: "b");
        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, elena);
        await service.SendAsync(group.Id, "Marcus, how are you?", TestContext.CancellationToken);

        GroupTurn? first = await service.PlanGroupTurnAsync(group.Id, new GroupCycle(GroupTrigger.UserMessage), TestContext.CancellationToken);
        ConversationReply failed = await service.ReplyInGroupAsync(group.Id, first!, cancellationToken: TestContext.CancellationToken);
        GroupCycle cycle = new GroupCycle(GroupTrigger.UserMessage).AfterFailure(first!);
        GroupTurn? second = await service.PlanGroupTurnAsync(group.Id, cycle, TestContext.CancellationToken);
        await service.ReplyInGroupAsync(group.Id, second!, cancellationToken: TestContext.CancellationToken);
        GroupTurn? third = await service.PlanGroupTurnAsync(group.Id, cycle.After(second!), TestContext.CancellationToken);

        Assert.AreEqual(marcus.Id, first!.Speaker);
        Assert.AreEqual(ReplyStatus.Unavailable, failed.Status);
        Assert.AreEqual(elena.Id, second!.Speaker);
        Assert.IsNull(third, "Marcus is not asked again this round.");
    }

    [TestMethod]
    public async Task AFollowUp_IsLeftToTheSpeakersModelOnceAndFallsBackWhenItsAnswerIsUnclear()
    {
        ScriptedAgent agent = new();
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, agents: agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        // The same model, so asking it costs no other model's load.
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext, model: "shared");
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext, model: "shared");
        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, elena);
        await service.SendAsync(group.Id, "Marcus, long day at the shop?", TestContext.CancellationToken);
        GroupTurn first = new(marcus.Id, GroupTurnReason.Addressed);
        await service.ReplyInGroupAsync(group.Id, first, cancellationToken: TestContext.CancellationToken);
        GroupCycle cycle = new GroupCycle(GroupTrigger.UserMessage).After(first);

        agent.Answer = request => ScriptedAgent.IsDecision(request) ? AgentReply.Success("YES") : null;
        GroupTurn? yes = await service.PlanGroupTurnAsync(group.Id, cycle, TestContext.CancellationToken);
        agent.Answer = request => ScriptedAgent.IsDecision(request) ? AgentReply.Success("NO") : null;
        GroupTurn? no = await service.PlanGroupTurnAsync(group.Id, cycle, TestContext.CancellationToken);
        agent.Answer = request => ScriptedAgent.IsDecision(request) ? AgentReply.Success("Hmm, hard to say") : null;
        GroupTurn? unclear = await service.PlanGroupTurnAsync(group.Id, cycle, TestContext.CancellationToken);
        agent.Answer = request => ScriptedAgent.IsDecision(request) ? throw new InvalidOperationException("The model crashed.") : null;
        GroupTurn? failed = await service.PlanGroupTurnAsync(group.Id, cycle, TestContext.CancellationToken);
        int asked = agent.DecisionRequests.Count;
        GroupTurn? alreadyAsked = await service.PlanGroupTurnAsync(group.Id, cycle with { ModelConsulted = true }, TestContext.CancellationToken);

        Assert.AreEqual(elena.Id, yes!.Speaker);
        Assert.IsTrue(yes.ModelConsulted);
        Assert.IsNull(no);
        Assert.IsTrue(unclear is null || unclear.Speaker == elena.Id, "An unclear answer is left to chance.");
        Assert.IsTrue(failed is null || failed.Speaker == elena.Id, "A failure is left to chance.");
        Assert.AreEqual(4, asked);
        Assert.HasCount(4, agent.DecisionRequests, "A model is asked at most once a round.");
        Assert.IsTrue(alreadyAsked is null || alreadyAsked.Speaker == elena.Id);
        Assert.IsTrue(agent.DecisionRequests.All(request => request.Precise && request.Model == "shared"));
    }

    [TestMethod]
    public async Task AFollowUpOnAnotherModel_IsNotDecidedByLoadingThatModel()
    {
        ScriptedAgent agent = new();
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, agents: agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext, model: "a");
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext, model: "b");
        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, elena);
        await service.SendAsync(group.Id, "Marcus, long day?", TestContext.CancellationToken);
        GroupTurn first = new(marcus.Id, GroupTurnReason.Addressed);
        await service.ReplyInGroupAsync(group.Id, first, cancellationToken: TestContext.CancellationToken);

        await service.PlanGroupTurnAsync(group.Id, new GroupCycle(GroupTrigger.UserMessage).After(first), TestContext.CancellationToken);

        Assert.IsEmpty(agent.DecisionRequests);
    }

    [TestMethod]
    public async Task GroupMemories_AreLearnedByEachContactFromTheUsersMessagesWithTheirOwnCheckpoint()
    {
        ScriptedAgent agent = new();
        agent.Answer = request => request.Precise && !ScriptedAgent.IsDecision(request)
            ? AgentReply.Success(request.Instructions.Contains("Marcus should", StringComparison.Ordinal) ? "- Aaron's dog is Biscuit." : "- Aaron's dog is named Biscuit.")
            : null;
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, agents: agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        IMemoryService memories = database.Services.GetRequiredService<IMemoryService>();
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext, model: "a");
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext, model: "b");
        Contact jo = await GroupTestKit.AddContactAsync(database, "Jo", TestContext, model: "c");
        SendMessageResult joChat = await service.SendAsync(jo.Id, "Hi Jo", TestContext.CancellationToken);
        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, elena);

        for (int index = 0; index < 6; index++)
        {
            await service.SendAsync(group.Id, $"Message {index}: " + new string('x', 480), TestContext.CancellationToken);
            await service.ReplyInGroupAsync(group.Id, new GroupTurn(index % 2 == 0 ? marcus.Id : elena.Id, GroupTurnReason.Answering), cancellationToken: TestContext.CancellationToken);
        }

        Conversation? updated = await service.UpdateMemoryAsync(group.Id, TestContext.CancellationToken);
        IReadOnlyList<Memory> marcusMemories = await memories.ListAsync(marcus.Id, TestContext.CancellationToken);
        IReadOnlyList<Memory> elenaMemories = await memories.ListAsync(elena.Id, TestContext.CancellationToken);
        AgentRequest[] extractions = [.. agent.Requests.Where(request => request.Precise && !ScriptedAgent.IsDecision(request))];

        Assert.IsNotNull(updated);
        Assert.IsNotNull(updated.MemoryThroughFor(marcus.Id));
        Assert.AreEqual(updated.MemoryThroughFor(marcus.Id), updated.MemoryThroughFor(elena.Id));
        Assert.AreSequenceEqual(new[] { "Aaron's dog is Biscuit." }, marcusMemories.Select(memory => memory.Text).ToArray());
        Assert.AreSequenceEqual(new[] { "Aaron's dog is named Biscuit." }, elenaMemories.Select(memory => memory.Text).ToArray());
        Assert.IsTrue(marcusMemories.Concat(elenaMemories).All(memory => memory.ConversationId == group.Id));
        Assert.IsEmpty(await memories.ListAsync(jo.Id, TestContext.CancellationToken));
        Assert.HasCount(2, extractions);
        Assert.IsTrue(extractions.All(request => request.Instructions.Contains("in a group chat with", StringComparison.Ordinal)));
        Assert.IsTrue(extractions.All(request => !request.Turns[0].Text.Contains(" here.", StringComparison.Ordinal)), "Only the user's messages are learned from.");

        // The checkpoints moved, so the same messages are not learned again.
        Assert.IsNull(await service.UpdateMemoryAsync(group.Id, TestContext.CancellationToken));
        Assert.HasCount(2, agent.Requests.Where(request => request.Precise && !ScriptedAgent.IsDecision(request)));

        // Deleting the group forgets what was learned in it, and nothing else.
        await service.DeleteAsync(group.Id, TestContext.CancellationToken);
        Assert.IsEmpty(await memories.ListAsync(marcus.Id, TestContext.CancellationToken));
        Assert.IsNotNull(await service.GetAsync(joChat.Conversation!.Id, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task MemoryEditor_AddsAMemoryForTheChosenGroupContactOnly()
    {
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext);
        IMemoryService memories = database.Services.GetRequiredService<IMemoryService>();
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext);
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext);
        Contact jo = await GroupTestKit.AddContactAsync(database, "Jo", TestContext);
        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, elena);

        MemorySaveResult forElena = await memories.AddAsync(group.Id, elena.Id, "Aaron loves hiking.", TestContext.CancellationToken);
        MemorySaveResult unspecified = await memories.AddAsync(group.Id, "Aaron loves hiking.", TestContext.CancellationToken);
        MemorySaveResult outsider = await memories.AddAsync(group.Id, jo.Id, "Aaron loves hiking.", TestContext.CancellationToken);

        Assert.IsTrue(forElena.Validation.IsValid);
        Assert.IsFalse(unspecified.Validation.IsValid);
        Assert.IsFalse(outsider.Validation.IsValid);
        Assert.HasCount(1, await memories.ListAsync(elena.Id, TestContext.CancellationToken));
        Assert.IsEmpty(await memories.ListAsync(marcus.Id, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task DeletingAContact_DeletesTheirGroupsButNotTheOtherContactsChat()
    {
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext);
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext);
        await service.SendAsync(marcus.Id, "Hi", TestContext.CancellationToken);
        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, elena);

        await database.Services.GetRequiredService<IContactService>().DeleteAsync(elena.Id, TestContext.CancellationToken);

        Assert.IsNull(await service.GetAsync(group.Id, TestContext.CancellationToken));
        Assert.IsNotNull(await service.FindByContactAsync(marcus.Id, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Migration_KeepsExistingChatsAndAttributesTheirReplies()
    {
        const string contactId = "11111111-1111-1111-1111-111111111111";
        const string conversationId = "22222222-2222-2222-2222-222222222222";
        const string userMessageId = "33333333-3333-3333-3333-333333333333";
        const string replyId = "44444444-4444-4444-4444-444444444444";
        string directory = Path.Combine(Path.GetTempPath(), $"HomiliaMessenger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            ApplicationDatabase database = new(Path.Combine(directory, "test.db"));
            Migration[] before = [new CreateContactsMigration(), new CreateConversationsMigration(), new AddRelationshipsAndUserProfileMigration(), new AddConversationMemoryMigration(), new AddContactVoiceMigration(), new AddContactMemoriesMigration(), new AddWebSearchMigration()];
            await new ApplicationDatabaseInitializer(database, before).InitializeAsync(TestContext.CancellationToken);

            await using (SqliteConnection connection = await database.OpenConnectionAsync(TestContext.CancellationToken))
            {
                await connection.ExecuteAsync("INSERT INTO Contact (Id, GivenName, IsFavorite) VALUES (@Id, 'Walt', 0)", new { Id = contactId });
                await connection.ExecuteAsync("INSERT INTO Conversation (Id, ContactId, StartedAtUtc, MemoryThroughMessageId) VALUES (@Id, @ContactId, '2026-01-01T00:00:00.0000000+00:00', @Through)", new { Id = conversationId, ContactId = contactId, Through = userMessageId });
                await connection.ExecuteAsync("INSERT INTO ChatMessage (Id, ConversationId, Sender, Text, SentAtUtc) VALUES (@Id, @ConversationId, 0, 'Hello', '2026-01-01T00:00:00.0000000+00:00')", new { Id = userMessageId, ConversationId = conversationId });
                await connection.ExecuteAsync("INSERT INTO ChatMessage (Id, ConversationId, Sender, Text, SentAtUtc, AgentProvider, AgentModel) VALUES (@Id, @ConversationId, 1, 'Hi there', '2026-01-01T00:00:01.0000000+00:00', 'Local', 'phi')", new { Id = replyId, ConversationId = conversationId });
                await connection.ExecuteAsync("INSERT INTO Memory (Id, ContactId, ConversationId, Text, Origin, CreatedAtUtc, UpdatedAtUtc) VALUES (@Id, @ContactId, @ConversationId, 'Aaron has a dog.', 1, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00')", new { Id = Guid.NewGuid().ToString("D"), ContactId = contactId, ConversationId = conversationId });
            }

            await new ApplicationDatabaseInitializer(database, [.. before, new AddGroupConversationsMigration()]).InitializeAsync(TestContext.CancellationToken);

            await using (SqliteConnection connection = await database.OpenConnectionAsync(TestContext.CancellationToken))
            {
                Assert.AreEqual(1L, await connection.ExecuteScalarAsync<long>("PRAGMA foreign_keys"));
                Assert.IsEmpty(await connection.QueryAsync("PRAGMA foreign_key_check"));
                Assert.AreEqual(8L, await connection.ExecuteScalarAsync<long>("SELECT MAX(Version) FROM SchemaMigration"));
            }

            await using (SqliteConnection pooled = await database.OpenConnectionAsync(TestContext.CancellationToken))
            {
                SqliteConnection.ClearPool(pooled);
            }

            using TestDatabase opened = await OpenExistingAsync(directory);
            IConversationService service = opened.Services.GetRequiredService<IConversationService>();
            Conversation? conversation = await service.GetAsync(new ConversationId(Guid.Parse(conversationId)), TestContext.CancellationToken);
            IReadOnlyList<ChatMessage> messages = await service.ListMessagesAsync(conversation!.Id, TestContext.CancellationToken);
            IReadOnlyList<Memory> memories = await opened.Services.GetRequiredService<IMemoryService>().ListAsync(new ContactId(Guid.Parse(contactId)), TestContext.CancellationToken);

            Assert.AreEqual(ConversationKind.Direct, conversation.Kind);
            Assert.AreEqual(new ContactId(Guid.Parse(contactId)), conversation.ContactId);
            Assert.AreEqual(new MessageId(Guid.Parse(userMessageId)), conversation.MemoryThrough);
            Assert.AreEqual(replyId, conversation.LastMessage!.Id.Value.ToString("D"));
            Assert.IsNull(messages[0].SenderContactId);
            Assert.AreEqual(new ContactId(Guid.Parse(contactId)), messages[1].SenderContactId);
            Assert.HasCount(1, memories);

            // The migrated database takes groups, and still holds one direct chat per contact.
            Contact elena = await GroupTestKit.AddContactAsync(opened, "Elena", TestContext);
            GroupCreationResult group = await service.CreateGroupAsync([new ContactId(Guid.Parse(contactId)), elena.Id], TestContext.CancellationToken);
            Assert.IsTrue(group.Validation.IsValid);
            Assert.AreEqual(conversation.Id, (await service.FindByContactAsync(new ContactId(Guid.Parse(contactId)), TestContext.CancellationToken))!.Id);
        }
        finally
        {
            // Opening the database hands it the directory, which it deletes when disposed.
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private Task<TestDatabase> OpenExistingAsync(string directory)
    {
        // Under the application's file name, so the tiers open the database just migrated.
        File.Move(Path.Combine(directory, "test.db"), Path.Combine(directory, "HomiliaMessenger.db"));

        return TestDatabase.OpenExistingAsync(directory, TestContext);
    }
}
