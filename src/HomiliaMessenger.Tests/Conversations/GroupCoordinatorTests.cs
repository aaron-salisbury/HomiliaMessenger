using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HomiliaMessenger.Tests.Conversations;

[TestClass]
public sealed class GroupCoordinatorTests
{
    private static readonly DateTimeOffset NOW = new(2026, 11, 16, 18, 0, 0, TimeSpan.Zero);

    private static readonly Contact MARCUS = new() { Id = ContactId.New(), GivenName = "Marcus", Personality = "Dry, practical mechanic." };
    private static readonly Contact ELENA = new() { Id = ContactId.New(), GivenName = "Elena", Personality = "Talkative, curious, and opinionated teacher." };
    private static readonly IReadOnlyList<Contact> CONTACTS = [MARCUS, ELENA];

    [TestMethod]
    [DataRow("Marcus, what do you think?", "Marcus")]
    [DataRow("Elena, do you agree with him?", "Elena")]
    [DataRow("what about you, Elena?", "Elena")]
    [DataRow("hey marcus how's it going", "Marcus")]
    [DataRow("@Elena look at this", "Elena")]
    [DataRow("No, Marcus, you're wrong.", "Marcus")]
    [DataRow("Long day. Elena how was class?", "Elena")]
    public void Addressed_RecognizesSpeakingToAContactByName(string text, string expected)
    {
        IReadOnlyList<Contact> addressed = GroupCoordinator.Addressed(text, CONTACTS);

        Assert.AreEqual(expected, addressed[0].DisplayName);
    }

    [TestMethod]
    public void Addressed_IgnoresANameMerelyMentioned()
    {
        Assert.IsEmpty(GroupCoordinator.Addressed("I told Marcus about it yesterday", CONTACTS));
        Assert.IsEmpty(GroupCoordinator.Addressed("Marcuses everywhere", CONTACTS));
        Assert.IsEmpty(GroupCoordinator.Addressed("Did Marcus fix your bike?", CONTACTS));
        Assert.IsEmpty(GroupCoordinator.Addressed("I asked Marcus", CONTACTS));
        Assert.IsEmpty(GroupCoordinator.Addressed("I told Marcus, and he laughed.", CONTACTS));
    }

    [TestMethod]
    public void Addressed_SkipsANameMentionedBeforeTheOneSpokenTo()
    {
        IReadOnlyList<Contact> addressed = GroupCoordinator.Addressed("I told Marcus yesterday. Elena, what do you think?", CONTACTS);

        Assert.AreSequenceEqual(new[] { ELENA.Id }, addressed.Select(contact => contact.Id).ToArray());
    }

    [TestMethod]
    public void AddressesEveryone_RecognizesTheWholeGroup()
    {
        Assert.IsTrue(GroupCoordinator.AddressesEveryone("What do you guys think?"));
        Assert.IsTrue(GroupCoordinator.AddressesEveryone("both of you are wrong"));
        Assert.IsFalse(GroupCoordinator.AddressesEveryone("I think so too."));
    }

    [TestMethod]
    public void DirectMention_TakesPriorityOverRelevance()
    {
        GroupCoordinator coordinator = new(new GroupCoordinatorOptions(), new Random(1));
        Conversation group = Group();
        // Marcus remembers plenty about the subject, but the user asked Elena.
        Dictionary<ContactId, int> memory = new() { [MARCUS.Id] = 3, [ELENA.Id] = 0 };

        GroupDecision decision = coordinator.Decide(new GroupSituation(group, CONTACTS, [User(group, "Elena, how's the engine sounding?")], new GroupCycle(GroupTrigger.UserMessage), NOW, memory));

        Assert.AreEqual(ELENA.Id, decision.Speaker);
        Assert.AreEqual(GroupTurnReason.Addressed, decision.Reason);
        Assert.IsTrue(decision.IsCertain);
    }

    [TestMethod]
    public void UnaddressedMessage_GoesToTheContactWhoRemembersItAndNotToEveryone()
    {
        Conversation group = Group();
        Dictionary<ContactId, int> memory = new() { [MARCUS.Id] = 3, [ELENA.Id] = 0 };
        List<ChatMessage> messages = [User(group, "Made good progress on the Atlas project today.")];

        for (int seed = 0; seed < 20; seed++)
        {
            GroupCoordinator coordinator = new(new GroupCoordinatorOptions(), new Random(seed));
            GroupDecision first = coordinator.Decide(new GroupSituation(group, CONTACTS, messages, new GroupCycle(GroupTrigger.UserMessage), NOW, memory));

            Assert.AreEqual(MARCUS.Id, first.Speaker);
            Assert.AreEqual(GroupTurnReason.Answering, first.Reason);

            // After Marcus answers, whether Elena adds something is left open rather than required.
            List<ChatMessage> answered = [.. messages, Contact(group, MARCUS, "Nice, the new parser finally works?")];
            GroupDecision second = coordinator.Decide(new GroupSituation(group, CONTACTS, answered, new GroupCycle(GroupTrigger.UserMessage, 1), NOW));

            Assert.AreEqual(ELENA.Id, second.Speaker);
            Assert.AreEqual(GroupTurnReason.FollowingUp, second.Reason);
            Assert.IsFalse(second.IsCertain);
        }
    }

    [TestMethod]
    public void AContactSpokenToByAnother_Answers()
    {
        GroupCoordinator coordinator = new(new GroupCoordinatorOptions(), new Random(1));
        Conversation group = Group();
        List<ChatMessage> messages = [User(group, "Anyone up for hiking?"), Contact(group, MARCUS, "Only if Elena drives. Elena, you in?")];

        GroupDecision decision = coordinator.Decide(new GroupSituation(group, CONTACTS, messages, new GroupCycle(GroupTrigger.UserMessage, 1), NOW));

        Assert.AreEqual(ELENA.Id, decision.Speaker);
        Assert.AreEqual(GroupTurnReason.Addressed, decision.Reason);
    }

    [TestMethod]
    public void AMessageForEveryone_IsAnsweredByEach()
    {
        GroupCoordinator coordinator = new(new GroupCoordinatorOptions(), new Random(1));
        Conversation group = Group();
        List<ChatMessage> messages = [User(group, "What are you two doing this weekend?"), Contact(group, ELENA, "Grading papers, sadly.")];

        GroupDecision decision = coordinator.Decide(new GroupSituation(group, CONTACTS, messages, new GroupCycle(GroupTrigger.UserMessage, 1), NOW));

        Assert.AreEqual(MARCUS.Id, decision.Speaker);
        Assert.IsTrue(decision.IsCertain);
    }

    [TestMethod]
    public void OnceEveryoneHasSpoken_TheGroupWaitsUnlessSomeoneIsAddressed()
    {
        GroupCoordinator coordinator = new(new GroupCoordinatorOptions(), new Random(1));
        Conversation group = Group();
        List<ChatMessage> messages = [User(group, "Long day."), Contact(group, MARCUS, "Same here."), Contact(group, ELENA, "Tell me about it.")];

        GroupDecision decision = coordinator.Decide(new GroupSituation(group, CONTACTS, messages, new GroupCycle(GroupTrigger.UserMessage, 2), NOW));

        Assert.IsNull(decision.Speaker);
    }

    [TestMethod]
    public void TheTurnLimit_EndsARoundEvenWhenContactsKeepAddressingEachOther()
    {
        GroupCoordinatorOptions options = new() { MaxAutomaticTurns = 3 };
        GroupCoordinator coordinator = new(options, new Random(1));
        Conversation group = Group();
        List<ChatMessage> messages = [User(group, "Marcus, Elena, settle this.")];
        GroupCycle cycle = new(GroupTrigger.UserMessage);
        int turns = 0;

        while (coordinator.Decide(new GroupSituation(group, CONTACTS, messages, cycle, NOW)) is { Speaker: { } speaker } decision)
        {
            Contact contact = CONTACTS.Single(candidate => candidate.Id == speaker);
            Contact other = CONTACTS.Single(candidate => candidate.Id != speaker);
            Assert.AreNotEqual(speaker, messages.Count > 1 ? group.SenderOf(messages[^1]) : null, "The same contact never writes twice in a row.");

            messages.Add(Contact(group, contact, $"{other.DisplayName}, you're wrong."));
            cycle = cycle.After(new GroupTurn(speaker, decision.Reason));
            turns++;
            Assert.IsLessThanOrEqualTo(options.MaxAutomaticTurns, turns);
        }

        Assert.AreEqual(options.MaxAutomaticTurns, turns);
    }

    [TestMethod]
    public void Initiation_WaitsForItsCooldownAndAQuietGroup()
    {
        GroupCoordinatorOptions options = new() { InitiationCooldown = TimeSpan.FromHours(4), InitiationQuietPeriod = TimeSpan.FromMinutes(30) };
        GroupCoordinator coordinator = new(options, new Random(1));
        Conversation group = Group();
        List<ChatMessage> quiet = [Contact(group, MARCUS, "Night all.", NOW.AddHours(-6))];

        Assert.IsTrue(coordinator.MayInitiate(group, [], NOW), "A new group may say hello.");
        Assert.IsTrue(coordinator.MayInitiate(group, quiet, NOW));
        Assert.IsFalse(coordinator.MayInitiate(group with { LastInitiationAtUtc = NOW.AddHours(-1) }, quiet, NOW), "Within the cooldown.");
        Assert.IsTrue(coordinator.MayInitiate(group with { LastInitiationAtUtc = NOW.AddHours(-5) }, quiet, NOW));
        Assert.IsFalse(coordinator.MayInitiate(group with { Initiation = GroupInitiation.Off }, quiet, NOW), "Turned off.");
        Assert.IsFalse(coordinator.MayInitiate(group, [Contact(group, MARCUS, "Night all.", NOW.AddMinutes(-5))], NOW), "Still talking.");
        Assert.IsFalse(coordinator.MayInitiate(group, [Contact(group, MARCUS, "You coming Saturday?", NOW.AddHours(-6))], NOW), "A question waits for the user.");
        Assert.IsFalse(coordinator.MayInitiate(group, [User(group, "Hello?", NOW.AddHours(-6))], NOW), "The user's message waits for an answer.");
    }

    [TestMethod]
    public void ANewGroup_IsGreetedByTheContactJustInvited()
    {
        GroupCoordinator coordinator = new(new GroupCoordinatorOptions(), new Random(1));
        Conversation group = Group();

        GroupDecision decision = coordinator.Decide(new GroupSituation(group, CONTACTS, [], new GroupCycle(GroupTrigger.Opened), NOW));

        Assert.AreEqual(ELENA.Id, decision.Speaker);
        Assert.AreEqual(GroupTurnReason.Greeting, decision.Reason);
        Assert.IsTrue(decision.IsCertain);
    }

    [TestMethod]
    public void Talkativeness_ReadsThePersonality()
    {
        Assert.IsGreaterThan(0, GroupCoordinator.Talkativeness(ELENA));
        Assert.IsLessThan(0, GroupCoordinator.Talkativeness(MARCUS with { Personality = "Quiet and reserved." }));
    }

    [TestMethod]
    public void ReadYesNo_AcceptsOnlyAClearAnswer()
    {
        Assert.IsTrue(GroupDecisions.ReadYesNo(" Yes."));
        Assert.IsFalse(GroupDecisions.ReadYesNo("**NO**"));
        Assert.IsNull(GroupDecisions.ReadYesNo("Maybe, if he feels like it"));
        Assert.IsNull(GroupDecisions.ReadYesNo(null));
    }

    private static Conversation Group()
    {
        return new Conversation
        {
            Id = ConversationId.New(),
            Kind = ConversationKind.Group,
            Participants = [new ConversationParticipant(MARCUS.Id), new ConversationParticipant(ELENA.Id)],
            StartedAtUtc = NOW.AddDays(-1)
        };
    }

    private static ChatMessage User(Conversation group, string text, DateTimeOffset? at = null)
    {
        return new ChatMessage { Id = MessageId.New(), ConversationId = group.Id, Sender = MessageSender.User, Text = text, SentAtUtc = at ?? NOW };
    }

    private static ChatMessage Contact(Conversation group, Contact contact, string text, DateTimeOffset? at = null)
    {
        return new ChatMessage { Id = MessageId.New(), ConversationId = group.Id, Sender = MessageSender.Contact, SenderContactId = contact.Id, Text = text, SentAtUtc = at ?? NOW };
    }
}
