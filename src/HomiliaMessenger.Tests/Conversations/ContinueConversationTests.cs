using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.Memories;
using HomiliaMessenger.Business.Modules.Profile;
using HomiliaMessenger.Presentation.Desktop.Models;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using HomiliaMessenger.Tests.Speech;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Conversations;

/// <summary>
/// Asking a contact, or a group, to keep talking without the user writing anything new.
/// </summary>
[TestClass]
public sealed class ContinueConversationTests
{
    private const string ATLAS = "Aaron is starting a new project at work called Atlas.";
    private const string CONTINUE_RULE = "hasn't written anything new";

    private static readonly DateTimeOffset NOW = new(2026, 11, 16, 18, 0, 0, TimeSpan.Zero);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Coordinator_ContinuedRound_FirstTurnIsCertainAndNotTheContactWhoJustWrote()
    {
        (Conversation group, Contact marcus, Contact elena) = Group();
        GroupCoordinator coordinator = new(new GroupCoordinatorOptions(), new Random(1));
        List<ChatMessage> messages = [User(group, "Long week."), FromContact(group, marcus, "Tell me about it.")];

        GroupDecision decision = coordinator.Decide(new GroupSituation(group, [marcus, elena], messages, new GroupCycle(GroupTrigger.Continued), NOW));

        Assert.AreEqual(elena.Id, decision.Speaker);
        Assert.AreEqual(GroupTurnReason.Continuing, decision.Reason);
        Assert.IsTrue(decision.IsCertain);
    }

    [TestMethod]
    public void Coordinator_ContinuedRound_AnswersWhoeverWasSpokenToOrTheUsersWaitingMessage()
    {
        (Conversation group, Contact marcus, Contact elena) = Group();
        GroupCoordinator coordinator = new(new GroupCoordinatorOptions(), new Random(1));

        GroupDecision addressed = coordinator.Decide(new GroupSituation(group, [marcus, elena], [User(group, "Hi"), FromContact(group, elena, "Marcus, you there?")], new GroupCycle(GroupTrigger.Continued), NOW));
        GroupDecision answering = coordinator.Decide(new GroupSituation(group, [marcus, elena], [FromContact(group, elena, "Hi"), User(group, "Elena, how was class?")], new GroupCycle(GroupTrigger.Continued), NOW));

        // Spoken to by name, Marcus writes next, and is asked to keep the conversation going: the user wrote nothing new.
        Assert.AreEqual(marcus.Id, addressed.Speaker);
        Assert.AreEqual(GroupTurnReason.Continuing, addressed.Reason);
        Assert.IsTrue(addressed.IsCertain);
        Assert.AreEqual(elena.Id, answering.Speaker);
        Assert.AreEqual(GroupTurnReason.Addressed, answering.Reason);
        Assert.IsTrue(answering.IsCertain);
    }

    [TestMethod]
    public void Coordinator_ContinuedRound_LaterTurnsAreOptionalAndBounded()
    {
        (Conversation group, Contact marcus, Contact elena) = Group();
        GroupCoordinator coordinator = new(new GroupCoordinatorOptions(), new Random(1));
        List<ChatMessage> messages = [User(group, "Long week."), FromContact(group, marcus, "Tell me about it."), FromContact(group, elena, "Mine too.")];

        GroupDecision second = coordinator.Decide(new GroupSituation(group, [marcus, elena], messages, new GroupCycle(GroupTrigger.Continued, TurnsTaken: 1), NOW));
        GroupDecision afterBoth = coordinator.Decide(new GroupSituation(group, [marcus, elena], [.. messages, FromContact(group, marcus, "Ha.")], new GroupCycle(GroupTrigger.Continued, TurnsTaken: 2), NOW));
        GroupDecision atLimit = coordinator.Decide(new GroupSituation(group, [marcus, elena], [.. messages, FromContact(group, marcus, "Elena, right?")], new GroupCycle(GroupTrigger.Continued, TurnsTaken: coordinator.Options.MaxAutomaticTurns), NOW));

        Assert.AreEqual(marcus.Id, second.Speaker);
        Assert.AreEqual(GroupTurnReason.FollowingUp, second.Reason);
        Assert.IsFalse(second.IsCertain, "Joining in after the first turn is up to the contact.");
        Assert.IsNull(afterBoth.Speaker);
        Assert.IsNull(atLimit.Speaker, "Even a contact spoken to by name waits once the round is over.");
    }

    [TestMethod]
    public async Task ContinueAsync_DirectChat_WritesOneReplyWithTheContactsModelPersonaAndMemories()
    {
        ScriptedAgent agent = new();
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, agents: agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        await database.Services.GetRequiredService<IUserProfileService>().SaveAsync(new UserProfile { Name = "Aaron" }, TestContext.CancellationToken);
        Contact walt = await GroupTestKit.AddContactAsync(database, "Walt", TestContext, model: "model-a", personality: "Dry humor, fixes old motorcycles.");
        SendMessageResult sent = await service.SendAsync(walt.Id, "Big news at work.", TestContext.CancellationToken);
        await database.Services.GetRequiredService<IMemoryService>().AddAsync(sent.Conversation!.Id, ATLAS, TestContext.CancellationToken);
        await service.ReplyAsync(sent.Conversation.Id, cancellationToken: TestContext.CancellationToken);
        agent.Answer = request => request.Instructions.Contains(CONTINUE_RULE, StringComparison.Ordinal) ? AgentReply.Success("So what's the project called?") : null;

        ConversationReply continued = await service.ContinueAsync(sent.Conversation.Id, cancellationToken: TestContext.CancellationToken);
        IReadOnlyList<ChatMessage> messages = await service.ListMessagesAsync(sent.Conversation.Id, TestContext.CancellationToken);
        AgentRequest request = agent.ReplyRequests[^1];

        Assert.AreEqual(ReplyStatus.Success, continued.Status, continued.Problem);
        Assert.AreSequenceEqual(new[] { MessageSender.User, MessageSender.Contact, MessageSender.Contact }, messages.Select(message => message.Sender).ToArray());
        Assert.AreEqual("So what's the project called?", messages[^1].Text);
        Assert.AreEqual(walt.Id, messages[^1].SenderContactId);
        Assert.AreEqual("model-a", messages[^1].Agent!.Model);
        Assert.AreEqual("model-a", request.Model);
        Assert.StartsWith("You are Walt, a real person texting with Aaron", request.Instructions);
        Assert.Contains("Dry humor", request.Instructions);
        Assert.Contains(ATLAS, request.Instructions);
        Assert.Contains("Aaron " + CONTINUE_RULE, request.Instructions);

        // The request to keep going is a note in this prompt only, after the contact's own last message.
        Assert.AreEqual(new AgentTurn(MessageSender.Contact, "Walt here."), request.Turns[^2]);
        Assert.AreEqual(new AgentTurn(MessageSender.User, "[Walt's turn to keep the conversation going]"), request.Turns[^1]);
        Assert.DoesNotContain(CONTINUE_RULE, agent.ReplyRequests[0].Instructions, "An ordinary reply is not asked to continue.");
    }

    [TestMethod]
    public async Task ContinueAsync_AReplyThatOnlyRepeatsTheLastOneOrFails_SavesNothing()
    {
        ScriptedAgent agent = new();
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, agents: agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await GroupTestKit.AddContactAsync(database, "Walt", TestContext, model: "model-a");
        SendMessageResult sent = await service.SendAsync(walt.Id, "Hi", TestContext.CancellationToken);
        await service.ReplyAsync(sent.Conversation!.Id, cancellationToken: TestContext.CancellationToken);

        // The scripted agent says "Walt here." again.
        ConversationReply repeated = await service.ContinueAsync(sent.Conversation.Id, cancellationToken: TestContext.CancellationToken);
        agent.Answer = _ => AgentReply.Unavailable("The model is not downloaded.");
        ConversationReply failed = await service.ContinueAsync(sent.Conversation.Id, cancellationToken: TestContext.CancellationToken);
        agent.Answer = _ => AgentReply.Success("[Walt's turn to keep the conversation going]");
        ConversationReply echoed = await service.ContinueAsync(sent.Conversation.Id, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(ReplyStatus.InvalidResponse, repeated.Status);
        Assert.Contains("nothing new", repeated.Problem!);
        Assert.AreEqual(ReplyStatus.Unavailable, failed.Status);
        Assert.AreEqual(ReplyStatus.InvalidResponse, echoed.Status);
        Assert.HasCount(2, await service.ListMessagesAsync(sent.Conversation.Id, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ContinuingInAGroup_WithAContact_WritesOneMessageAsThem()
    {
        ScriptedAgent agent = new();
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, agents: agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext, model: "model-a");
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext, model: "model-b");
        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, elena);
        await service.SendAsync(group.Id, "Marcus, how was the shop?", TestContext.CancellationToken);
        await service.ReplyInGroupAsync(group.Id, new GroupTurn(marcus.Id, GroupTurnReason.Addressed), cancellationToken: TestContext.CancellationToken);
        agent.Answer = request => request.Instructions.Contains(CONTINUE_RULE, StringComparison.Ordinal) ? AgentReply.Success("Oh, and the Triumph runs again.") : null;

        ConversationReply reply = await service.ReplyInGroupAsync(group.Id, new GroupTurn(marcus.Id, GroupTurnReason.Continuing), cancellationToken: TestContext.CancellationToken);
        IReadOnlyList<ChatMessage> messages = await service.ListMessagesAsync(group.Id, TestContext.CancellationToken);
        AgentRequest request = agent.ReplyRequests[^1];

        Assert.AreEqual(ReplyStatus.Success, reply.Status, reply.Problem);
        Assert.AreSequenceEqual(new ContactId?[] { null, marcus.Id, marcus.Id }, messages.Select(group.SenderOf).ToArray());
        Assert.AreEqual("model-a", request.Model);
        Assert.StartsWith("You are Marcus, a real person in a group chat", request.Instructions);
        Assert.AreEqual(new AgentTurn(MessageSender.User, "[Marcus's turn to write in the group chat]"), request.Turns[^1]);
    }

    [TestMethod]
    public async Task ContinuedRound_WorksWithInitiationOffAndStaysWithinTheTurnLimit()
    {
        ScriptedAgent agent = new();
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, agents: agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext, model: "model-a");
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext, model: "model-a");
        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, elena);
        await service.SetInitiationAsync(group.Id, GroupInitiation.Off, TestContext.CancellationToken);
        await service.SendAsync(group.Id, "Marcus, how was the shop?", TestContext.CancellationToken);
        await service.ReplyInGroupAsync(group.Id, new GroupTurn(marcus.Id, GroupTurnReason.Addressed), cancellationToken: TestContext.CancellationToken);
        int round = 0;
        agent.Answer = request => ScriptedAgent.IsDecision(request) ? AgentReply.Success("YES") : request.Precise ? null : AgentReply.Success($"{ScriptedAgent.SpeakerOf(request)} again, {Interlocked.Increment(ref round)}.");

        IReadOnlyList<ChatMessage> replies = await GroupTestKit.RunRoundAsync(service, group.Id, GroupTrigger.Continued, TestContext.CancellationToken);
        IReadOnlyList<ChatMessage> messages = await service.ListMessagesAsync(group.Id, TestContext.CancellationToken);
        Conversation stored = (await service.GetAsync(group.Id, TestContext.CancellationToken))!;

        Assert.IsInRange(1, new GroupCoordinatorOptions().MaxAutomaticTurns, replies.Count);
        Assert.AreEqual(elena.Id, group.SenderOf(replies[0]), "Marcus wrote last, so Elena continues.");
        Assert.IsTrue(replies.All(reply => reply.Sender == MessageSender.Contact));
        Assert.IsTrue(replies.Zip(replies.Skip(1)).All(pair => pair.First.SenderContactId != pair.Second.SenderContactId));
        Assert.HasCount(1, messages.Where(message => message.Sender == MessageSender.User), "No message is written for the user.");
        Assert.Contains(CONTINUE_RULE, agent.ReplyRequests[^replies.Count].Instructions);
        Assert.AreEqual(GroupInitiation.Off, stored.Initiation);
        Assert.IsNull(stored.LastInitiationAtUtc, "Continuing does not use up the group's chance to start a conversation.");
    }

    [TestMethod]
    public async Task ContinuedRound_WhenTheChosenContactCannotWrite_TheOtherContinues()
    {
        ScriptedAgent agent = new();
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, agents: agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact marcus = await GroupTestKit.AddContactAsync(database, "Marcus", TestContext, model: "model-a");
        Contact elena = await GroupTestKit.AddContactAsync(database, "Elena", TestContext, model: "model-b");
        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, marcus, elena);
        await service.SendAsync(group.Id, "Marcus, how was the shop?", TestContext.CancellationToken);
        await service.ReplyInGroupAsync(group.Id, new GroupTurn(marcus.Id, GroupTurnReason.Addressed), cancellationToken: TestContext.CancellationToken);
        agent.Answer = request => request.Precise ? null
            : ScriptedAgent.SpeakerOf(request) == "Elena" ? AgentReply.Unavailable("Elena's model has not been downloaded yet.")
            : AgentReply.Success("Anyway, the Triumph runs again.");
        GroupCycle cycle = new(GroupTrigger.Continued);

        GroupTurn? first = await service.PlanGroupTurnAsync(group.Id, cycle, TestContext.CancellationToken);
        ConversationReply failed = await service.ReplyInGroupAsync(group.Id, first!, cancellationToken: TestContext.CancellationToken);
        cycle = cycle.AfterFailure(first!);
        GroupTurn? fallback = await service.PlanGroupTurnAsync(group.Id, cycle, TestContext.CancellationToken);
        ConversationReply reply = await service.ReplyInGroupAsync(group.Id, fallback!, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(elena.Id, first!.Speaker);
        Assert.AreEqual(ReplyStatus.Unavailable, failed.Status);
        Assert.AreEqual(marcus.Id, fallback!.Speaker);
        Assert.AreEqual(GroupTurnReason.Continuing, fallback.Reason);
        Assert.AreEqual(ReplyStatus.Success, reply.Status, reply.Problem);
        Assert.IsNull(await service.PlanGroupTurnAsync(group.Id, cycle.After(fallback), TestContext.CancellationToken), "With Elena unavailable, nobody follows up.");
    }

    [TestMethod]
    public async Task ContinueButton_ShowsOnceBeneathTheLatestContactMessage()
    {
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext, TimeSpan.FromMilliseconds(300));
        Contact walt = await ConversationServiceTests.AddContactAsync(database, "Walt", TestContext);
        MessengerViewModel messenger = CreateViewModel(database);

        await messenger.InitializeAsync();
        await messenger.StartChatCommand.ExecuteAsync(walt);
        Assert.IsFalse(messenger.CanContinue, "Nothing to continue before the contact has written.");

        messenger.DraftText = "Hi";
        await messenger.SendCommand.ExecuteAsync(null);
        Assert.IsEmpty(messenger.Messages.Where(item => item.ShowsContinue));
        await WaitUntilAsync(() => messenger.Messages.Count == 2 && !messenger.IsReplyPending);
        Assert.AreSame(messenger.Messages[1], messenger.Messages.Single(item => item.ShowsContinue));
        Assert.IsTrue(messenger.Messages[1].ContinuesDirectChat);
        Assert.IsFalse(messenger.Messages[1].ContinuesGroup);
        Assert.IsTrue(messenger.ContinueCommand.CanExecute(null));

        // A newer message from the user leaves it where it is, disabled while the contact replies.
        messenger.DraftText = "You there?";
        await messenger.SendCommand.ExecuteAsync(null);
        Assert.AreSame(messenger.Messages[1], messenger.Messages.Single(item => item.ShowsContinue));
        Assert.IsFalse(messenger.CanContinue);
        Assert.IsFalse(messenger.ContinueCommand.CanExecute(null));

        // The contact's reply takes it.
        await WaitUntilAsync(() => messenger.Messages.Count == 4 && !messenger.IsReplyPending);
        Assert.AreSame(messenger.Messages[3], messenger.Messages.Single(item => item.ShowsContinue));
        Assert.IsTrue(messenger.CanContinue);

        // Another chat shows its own.
        Contact rose = await ConversationServiceTests.AddContactAsync(database, "Rose", TestContext);
        await messenger.StartChatCommand.ExecuteAsync(rose);
        Assert.IsEmpty(messenger.Messages);
        Assert.IsFalse(messenger.CanContinue);
    }

    [TestMethod]
    public async Task Continue_InADirectChat_WritesOneSpokenReplyEvenWhenClickedRepeatedly()
    {
        ScriptedAgent agent = new() { Delay = TimeSpan.FromMilliseconds(200) };
        int count = 0;
        agent.Answer = request => request.Precise ? null : AgentReply.Success($"Message {Interlocked.Increment(ref count)}.");
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, agents: agent);
        Contact walt = await GroupTestKit.AddContactAsync(database, "Walt", TestContext, model: "model-a", voice: "am_adam");
        FakeSpeech speech = new();
        MessengerViewModel messenger = CreateViewModel(database, speech);

        await messenger.InitializeAsync();
        await messenger.StartChatCommand.ExecuteAsync(walt);
        messenger.DraftText = "Hi";
        await messenger.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => messenger.Messages.Count == 2 && !messenger.IsReplyPending);
        messenger.ToggleSpeechCommand.Execute(null);

        messenger.ContinueCommand.Execute(null);
        Assert.IsTrue(messenger.IsContactTyping);
        messenger.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => messenger.Messages.Count == 3 && !messenger.IsReplyPending);
        await Task.Delay(400, TestContext.CancellationToken);

        IReadOnlyList<ChatMessage> stored = await database.Services.GetRequiredService<IConversationService>().ListMessagesAsync(messenger.ActiveConversationId!.Value, TestContext.CancellationToken);
        Assert.AreSequenceEqual(new[] { MessageSender.User, MessageSender.Contact, MessageSender.Contact }, stored.Select(message => message.Sender).ToArray());
        Assert.HasCount(3, messenger.Messages);
        Assert.AreSame(messenger.Messages[2], messenger.Messages.Single(item => item.ShowsContinue));
        Assert.HasCount(1, speech.Spoken);
        Assert.AreEqual(("Message 2.", "am_adam"), speech.Spoken[0]);
        Assert.HasCount(2, agent.ReplyRequests);
    }

    [TestMethod]
    public async Task Continue_AFailureIsShownAndTheButtonWorksAgain()
    {
        ScriptedAgent agent = new();
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, agents: agent);
        Contact walt = await GroupTestKit.AddContactAsync(database, "Walt", TestContext, model: "model-a");
        MessengerViewModel messenger = CreateViewModel(database);

        await messenger.InitializeAsync();
        await messenger.StartChatCommand.ExecuteAsync(walt);
        messenger.DraftText = "Hi";
        await messenger.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => messenger.Messages.Count == 2 && !messenger.IsReplyPending);

        agent.Answer = _ => throw new InvalidOperationException("The model crashed.");
        messenger.ContinueCommand.Execute(null);
        await WaitUntilAsync(() => !messenger.IsReplyPending);

        Assert.IsTrue(messenger.HasReplyProblem);
        Assert.Contains("The model crashed", messenger.ReplyProblem!);
        Assert.HasCount(2, messenger.Messages);
        Assert.IsTrue(messenger.ContinueCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task ContinueInAGroup_WithAContactOrTheGroup_AndLeavingStopsIt()
    {
        ScriptedAgent agent = new();
        int count = 0;
        agent.Answer = request => request.Precise ? null : AgentReply.Success($"{ScriptedAgent.SpeakerOf(request)} {Interlocked.Increment(ref count)}.");
        using TestDatabase database = await GroupTestKit.CreateDatabaseAsync(TestContext, agents: agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await GroupTestKit.AddContactAsync(database, "Walt", TestContext, model: "model-a");
        Contact rose = await GroupTestKit.AddContactAsync(database, "Rose", TestContext, model: "model-a");
        Conversation group = await GroupTestKit.CreateGroupAsync(database, TestContext, walt, rose);
        await service.SetInitiationAsync(group.Id, GroupInitiation.Off, TestContext.CancellationToken);
        await service.SendAsync(group.Id, "Walt, how was the shop?", TestContext.CancellationToken);
        await service.ReplyInGroupAsync(group.Id, new GroupTurn(walt.Id, GroupTurnReason.Addressed), cancellationToken: TestContext.CancellationToken);
        MessengerViewModel messenger = CreateViewModel(database);

        await messenger.InitializeAsync();
        messenger.SelectedConversation = messenger.Conversations.Single(item => item.IsGroup);
        await WaitUntilAsync(() => messenger.IsGroupThread && messenger.Messages.Count == 2 && !messenger.IsReplyPending);
        MessageItem target = messenger.Messages.Single(item => item.ShowsContinue);
        Assert.IsTrue(target.ContinuesGroup);
        Assert.IsFalse(target.ContinuesDirectChat);
        Assert.AreEqual("Walt", target.SenderName, "The menu offers to continue with whoever wrote last.");

        // Walt continues, and only Walt.
        messenger.ContinueWithCommand.Execute(target.Sender);
        await WaitUntilAsync(() => messenger.Messages.Count == 3 && !messenger.IsReplyPending);
        await Task.Delay(200, TestContext.CancellationToken);
        Assert.HasCount(3, messenger.Messages);
        Assert.AreEqual(walt.Id, messenger.Messages[2].Sender!.Id);
        Assert.AreEqual("Walt", messenger.Messages.Single(item => item.ShowsContinue).SenderName);

        // The group decides: Rose, since Walt just wrote; her model then declines to add more.
        messenger.ContinueGroupCommand.Execute(null);
        await WaitUntilAsync(() => messenger.Messages.Count == 4 && !messenger.IsReplyPending);
        await Task.Delay(200, TestContext.CancellationToken);
        Assert.HasCount(4, messenger.Messages);
        Assert.AreEqual(rose.Id, messenger.Messages[3].Sender!.Id);
        Assert.AreEqual("Rose", messenger.Messages.Single(item => item.ShowsContinue).SenderName);
        Assert.IsTrue(messenger.Messages.Skip(1).All(item => !item.IsFromUser), "No message is written for the user.");

        // Leaving the group while it continues stops it.
        agent.Delay = TimeSpan.FromMilliseconds(400);
        messenger.ContinueGroupCommand.Execute(null);
        await WaitUntilAsync(() => messenger.IsContactTyping);
        await messenger.StartChatCommand.ExecuteAsync(walt);
        await Task.Delay(800, TestContext.CancellationToken);
        Assert.HasCount(4, await service.ListMessagesAsync(group.Id, TestContext.CancellationToken));
    }

    private static (Conversation Group, Contact Marcus, Contact Elena) Group()
    {
        Contact marcus = new() { Id = ContactId.New(), GivenName = "Marcus", Personality = "Dry, practical mechanic." };
        Contact elena = new() { Id = ContactId.New(), GivenName = "Elena", Personality = "Talkative, curious, and opinionated teacher." };
        Conversation group = new()
        {
            Id = ConversationId.New(),
            Kind = ConversationKind.Group,
            Participants = [new ConversationParticipant(marcus.Id), new ConversationParticipant(elena.Id)],
            StartedAtUtc = NOW.AddDays(-1),
            Initiation = GroupInitiation.Off
        };

        return (group, marcus, elena);
    }

    private static ChatMessage User(Conversation group, string text)
    {
        return new ChatMessage { Id = MessageId.New(), ConversationId = group.Id, Sender = MessageSender.User, Text = text, SentAtUtc = NOW };
    }

    private static ChatMessage FromContact(Conversation group, Contact contact, string text)
    {
        return new ChatMessage { Id = MessageId.New(), ConversationId = group.Id, Sender = MessageSender.Contact, SenderContactId = contact.Id, Text = text, SentAtUtc = NOW };
    }

    private static MessengerViewModel CreateViewModel(TestDatabase database, FakeSpeech? speech = null)
    {
        return new MessengerViewModel(
            database.Services.GetRequiredService<IConversationService>(),
            database.Services.GetRequiredService<IContactService>(),
            database.Services.GetRequiredService<IMemoryService>(), speech);
    }

    private async Task WaitUntilAsync(Func<bool> condition)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail("Timed out waiting for the messenger to update.");
            }

            await Task.Delay(10, TestContext.CancellationToken);
        }
    }
}
