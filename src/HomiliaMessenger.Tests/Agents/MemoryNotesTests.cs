using FluentValidation.Results;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.Profile;
using HomiliaMessenger.Tests.Conversations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Agents;

[TestClass]
public sealed class MemoryNotesTests
{
    private static readonly Contact WALT = new() { Id = ContactId.New(), GivenName = "Walt", Birthday = new DateOnly(1948, 3, 14) };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void FoldBoundary_WaitsUntilTheUnrememberedMessagesOutgrowTheBudget()
    {
        List<ChatMessage> messages = Exchange(10, 100);

        int? notYet = MemoryNotes.FoldBoundary(messages, 0);
        messages.AddRange(Exchange(3, 100));
        int? boundary = MemoryNotes.FoldBoundary(messages, 0);

        Assert.IsNull(notYet);
        Assert.IsNotNull(boundary);
        Assert.IsLessThanOrEqualTo(MemoryNotes.KEEP_CHARACTERS, messages.Skip(boundary.Value).Sum(message => message.Text.Length));
        Assert.AreEqual(MessageSender.User, messages[boundary.Value].Sender);
        Assert.IsNull(MemoryNotes.FoldBoundary(messages, boundary.Value));
    }

    [TestMethod]
    public void FoldBoundary_CondensesALongHistoryABatchAtATime()
    {
        List<ChatMessage> messages = Exchange(100, 100);
        int start = 0;
        int batches = 0;

        while (MemoryNotes.FoldBoundary(messages, start) is { } boundary)
        {
            long batch = messages.Skip(start).Take(boundary - start).Sum(message => (long)message.Text.Length);
            Assert.IsLessThanOrEqualTo(MemoryNotes.FOLD_AFTER_CHARACTERS + 100, batch);
            Assert.AreEqual(MessageSender.User, messages[boundary].Sender);
            start = boundary;
            batches++;
        }

        Assert.IsGreaterThan(5, batches);
        Assert.IsLessThanOrEqualTo(MemoryNotes.FOLD_AFTER_CHARACTERS, messages.Skip(start).Sum(message => (long)message.Text.Length));
    }

    [TestMethod]
    public void FoldBoundary_AlwaysKeepsTheLatestMessage()
    {
        List<ChatMessage> messages = [Message(MessageSender.User, new string('x', MemoryNotes.FOLD_AFTER_CHARACTERS + 1))];
        messages.Add(Message(MessageSender.User, new string('y', MemoryNotes.KEEP_CHARACTERS + 1)));

        Assert.AreEqual(1, MemoryNotes.FoldBoundary(messages, 0));
    }

    [TestMethod]
    public void FoldBoundary_KeepsALongLastReplyWithTheMessageItAnswered()
    {
        List<ChatMessage> messages = Exchange(8, 100);
        messages.Add(Message(MessageSender.User, "Tell me a story?"));
        messages.Add(Message(MessageSender.Contact, new string('s', MemoryNotes.KEEP_CHARACTERS + 100)));

        int? boundary = MemoryNotes.FoldBoundary(messages, 0);

        Assert.AreEqual(messages.Count - 2, boundary);
    }

    [TestMethod]
    public void FirstUnremembered_FollowsTheLastCondensedMessage()
    {
        List<ChatMessage> messages = Exchange(3, 10);

        Assert.AreEqual(0, MemoryNotes.FirstUnremembered(messages, null));
        Assert.AreEqual(4, MemoryNotes.FirstUnremembered(messages, messages[3].Id));
        Assert.AreEqual(0, MemoryNotes.FirstUnremembered(messages, MessageId.New()));
    }

    [TestMethod]
    public void ExtractRequest_GivesWhatIsKnownAndTheMessagesAsATranscript()
    {
        List<ChatMessage> folding = [Message(MessageSender.User, "My dog Biscuit is sick."), Message(MessageSender.Contact, "Poor pup.\nGive him a hug.")];

        AgentRequest request = MemoryNotes.ExtractRequest(WALT, new UserProfile { Name = "Aaron" }, "- Aaron is a nurse.", folding, "phi-3.5-mini");

        Assert.AreEqual("phi-3.5-mini", request.Model);
        Assert.Contains("list the lasting facts Walt should remember later", request.Instructions);
        Assert.Contains("at most 3 facts", request.Instructions);
        Assert.HasCount(1, request.Turns);
        Assert.Contains("Already known:\n- Aaron is a nurse.".ReplaceLineEndings(), request.Turns[0].Text.ReplaceLineEndings());
        Assert.Contains("Messages from Aaron:\n- My dog Biscuit is sick.".ReplaceLineEndings(), request.Turns[0].Text.ReplaceLineEndings());
        Assert.DoesNotContain("Poor pup", request.Turns[0].Text);
        Assert.IsTrue(request.Precise);
    }

    [TestMethod]
    public void Facts_KeepsTheListedFactsAndDropsHeadingsCommentaryAndNone()
    {
        string raw = "Facts:\n* Aaron's dog is Biscuit.\n\n• Lunch is set for Saturday.\nAaron is a nurse.\nWord count: 12\n---\nThis list keeps the key facts.";

        IReadOnlyList<string> facts = MemoryNotes.Facts(raw);

        Assert.AreSequenceEqual(new[] { "- Aaron's dog is Biscuit.", "- Lunch is set for Saturday.", "- Aaron is a nurse." }, facts.ToArray());
        Assert.IsEmpty(MemoryNotes.Facts("- none"));
        Assert.IsEmpty(MemoryNotes.Facts("Nothing new worth remembering."));
        Assert.IsEmpty(MemoryNotes.Facts("(Note: nothing to add.)"));
        Assert.HasCount(MemoryNotes.MAX_FACTS_PER_FOLD, MemoryNotes.Facts("- a\n- b\n- c\n- d\n- e"));
    }

    [TestMethod]
    public void Append_AddsNewFactsAndLetsTheOldestGoWhenFull()
    {
        string? notes = MemoryNotes.Append("- Aaron's dog is Biscuit.", ["- aaron's dog is biscuit.", "- Jen is moving to Portland."]);
        string? full = MemoryNotes.Append(notes, [.. Enumerable.Range(0, 20).Select(i => $"- Person{i} lives in City{i} " + new string((char)('a' + i), 60))]);

        Assert.AreEqual("- Aaron's dog is Biscuit.\n- Jen is moving to Portland.", notes);
        Assert.IsLessThanOrEqualTo(Conversation.MAX_MEMORY_NOTES_LENGTH, full!.Length);
        Assert.DoesNotContain("Biscuit", full);
        Assert.EndsWith(new string((char)('a' + 19), 60), full);
        Assert.IsNull(MemoryNotes.Append(null, []));
    }

    [TestMethod]
    [DataRow("- Jen is moving to Portland in March.", "- jen is moving to Portland, in March", true)]
    [DataRow("- Jen is moving to Portland in March.", "- Jen moves to Portland in March.", false)]
    [DataRow("- Aaron starts his new nursing job in Denver on Monday.", "- Aaron starts his new nursing job in Denver on Tuesday.", false)]
    [DataRow("- Aaron works for Sam.", "- Sam works for Aaron.", false)]
    [DataRow("- Aaron was a nurse.", "- Aaron is a nurse.", false)]
    [DataRow("- Aaron likes coffee.", "- Aaron no longer likes coffee.", false)]
    public void IsSameFact_MatchesOnlyTheSameFactWrittenAgain(string known, string fact, bool same)
    {
        Assert.AreEqual(same, MemoryNotes.IsSameFact(known, fact));
        Assert.IsNull(MemoryNotes.Append(null, []));
    }

    [TestMethod]
    [DataRow("Sounds good. See you Satur", "Sounds good.")]
    [DataRow("- Aaron is a nurse.\n- Jen is mov", "- Aaron is a nurse.")]
    [DataRow("no ending at all", "no ending at all")]
    public void WithoutUnfinishedEnd_EndsACutOffReplyAtItsLastSentenceOrLine(string text, string expected)
    {
        Assert.AreEqual(expected, HomiliaMessenger.Integrations.LocalModels.OnnxModelHost.WithoutUnfinishedEnd(text));
    }

    [TestMethod]
    public void Instructions_IncludeWhatThePersonaRemembers()
    {
        string instructions = PersonaFraming.Instructions(WALT, new UserProfile { Name = "Aaron" }, "- Aaron's dog is Biscuit.", new DateOnly(2026, 10, 31));

        Assert.Contains("What you remember from earlier in this conversation:", instructions);
        Assert.Contains("- Aaron's dog is Biscuit.", instructions);
        Assert.Contains("don't ask about things Aaron already told you", instructions);
    }

    [TestMethod]
    public async Task UpdateMemoryAsync_CondensesOlderMessagesAndRepliesUseTheNotesWithTheRest()
    {
        NotingAgent agent = new();
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext, default, agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await SaveWaltAsync(database);
        ConversationId conversation = await ChatAsync(service, walt, 14);

        Conversation? updated = await service.UpdateMemoryAsync(conversation, TestContext.CancellationToken);
        Conversation? again = await service.UpdateMemoryAsync(conversation, TestContext.CancellationToken);
        await service.SendAsync(walt.Id, "Remember my dog's name?", TestContext.CancellationToken);
        await service.ReplyAsync(conversation, TestContext.CancellationToken);

        Assert.IsNotNull(updated);
        Assert.AreEqual("- Aaron's dog is Biscuit.", updated.MemoryNotes);
        Assert.IsNull(again);
        AgentRequest noteRequest = agent.Requests.Single(request => request.Instructions.Contains("should remember later"));
        Assert.Contains("message 0 ", noteRequest.Turns[0].Text);
        AgentRequest reply = agent.Requests[^1];
        Assert.Contains("- Aaron's dog is Biscuit.", reply.Instructions);
        Assert.DoesNotContain(turn => turn.Text.Contains("message 0 "), reply.Turns);
        Assert.AreEqual("Remember my dog's name?", reply.Turns[^1].Text);
    }

    [TestMethod]
    public async Task UpdateMemoryAsync_MovesPastMessagesWithNothingWorthRemembering()
    {
        NotingAgent agent = new() { Facts = "- none" };
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext, default, agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await SaveWaltAsync(database);
        ConversationId conversation = await ChatAsync(service, walt, 14);

        Conversation? updated = await service.UpdateMemoryAsync(conversation, TestContext.CancellationToken);

        Assert.IsNotNull(updated);
        Assert.IsNull(updated.MemoryNotes);
        Assert.IsNotNull(updated.MemoryThrough);
        Assert.IsNull(await service.UpdateMemoryAsync(conversation, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task UpdateMemoryAsync_LeavesShortChatsAndSimulatedContactsAlone()
    {
        NotingAgent agent = new();
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext, default, agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await SaveWaltAsync(database);
        Contact rose = await ConversationServiceTests.AddContactAsync(database, "Rose", TestContext);
        ConversationId shortChat = await ChatAsync(service, walt, 2);
        ConversationId simulated = await ChatAsync(service, rose, 14);

        Assert.IsNull(await service.UpdateMemoryAsync(shortChat, TestContext.CancellationToken));
        Assert.IsNull(await service.UpdateMemoryAsync(simulated, TestContext.CancellationToken));
        Assert.DoesNotContain(request => request.Instructions.Contains("should remember later"), agent.Requests);
    }

    [TestMethod]
    public async Task UpdateMemoryAsync_DoesNotOverwriteNotesTheUserEditedMeanwhile()
    {
        NotingAgent agent = new();
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext, default, agent);
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await SaveWaltAsync(database);
        ConversationId conversation = await ChatAsync(service, walt, 14);
        agent.WhileNoting = () => service.SaveMemoryNotesAsync(conversation, "- Aaron is allergic to cats.", null, TestContext.CancellationToken);

        Conversation? updated = await service.UpdateMemoryAsync(conversation, TestContext.CancellationToken);
        Conversation? stored = await service.FindByContactAsync(walt.Id, TestContext.CancellationToken);

        Assert.IsNull(updated);
        Assert.AreEqual("- Aaron is allergic to cats.", stored!.MemoryNotes);
        Assert.IsNull(stored.MemoryThrough);
    }

    [TestMethod]
    public async Task SaveMemoryNotesAsync_SavesCorrectionsUnlessTooLongOrOutdated()
    {
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext, default, new NotingAgent());
        IConversationService service = database.Services.GetRequiredService<IConversationService>();
        Contact walt = await SaveWaltAsync(database);
        ConversationId conversation = await ChatAsync(service, walt, 1);

        ValidationResult saved = await service.SaveMemoryNotesAsync(conversation, "  - Aaron is a nurse.  ", null, TestContext.CancellationToken);
        ValidationResult outdated = await service.SaveMemoryNotesAsync(conversation, "- Something else.", null, TestContext.CancellationToken);
        ValidationResult tooLong = await service.SaveMemoryNotesAsync(conversation, new string('x', Conversation.MAX_MEMORY_NOTES_LENGTH + 1), "- Aaron is a nurse.", TestContext.CancellationToken);
        ValidationResult cleared = await service.SaveMemoryNotesAsync(conversation, "   ", "- Aaron is a nurse.", TestContext.CancellationToken);
        Conversation? stored = await service.FindByContactAsync(walt.Id, TestContext.CancellationToken);

        Assert.IsTrue(saved.IsValid);
        Assert.IsFalse(outdated.IsValid);
        Assert.IsFalse(tooLong.IsValid);
        Assert.IsTrue(cleared.IsValid);
        Assert.IsNull(stored!.MemoryNotes);
    }

    private async Task<Contact> SaveWaltAsync(TestDatabase database)
    {
        Contact walt = WALT with { Id = ContactId.New(), Agent = new AgentAssignment("Noting", "model") };
        await database.Services.GetRequiredService<IContactService>().SaveAsync(walt, TestContext.CancellationToken);
        await database.Services.GetRequiredService<IUserProfileService>().SaveAsync(new UserProfile { Name = "Aaron" }, TestContext.CancellationToken);

        return walt;
    }

    /// <summary>
    /// Sends <paramref name="exchanges"/> messages of about 200 characters, each answered.
    /// </summary>
    private async Task<ConversationId> ChatAsync(IConversationService service, Contact contact, int exchanges)
    {
        ConversationId? conversation = null;

        for (int i = 0; i < exchanges; i++)
        {
            SendMessageResult sent = await service.SendAsync(contact.Id, $"message {i} " + new string('x', 190), TestContext.CancellationToken);
            conversation = sent.Conversation!.Id;
            await service.ReplyAsync(conversation.Value, TestContext.CancellationToken);
        }

        return conversation!.Value;
    }

    private static List<ChatMessage> Exchange(int pairs, int length)
    {
        List<ChatMessage> messages = [];

        for (int i = 0; i < pairs; i++)
        {
            messages.Add(Message(MessageSender.User, new string('u', length)));
            messages.Add(Message(MessageSender.Contact, new string('c', length)));
        }

        return messages;
    }

    private static ChatMessage Message(MessageSender sender, string text)
    {
        return new ChatMessage { Id = MessageId.New(), ConversationId = ConversationId.New(), Sender = sender, Text = text, SentAtUtc = DateTimeOffset.UtcNow };
    }

    /// <summary>
    /// Replies "Sure." to chats and writes fixed notes when asked to, recording every request.
    /// </summary>
    private sealed class NotingAgent : IConversationAgent
    {
        public string Provider => "Noting";

        public List<AgentRequest> Requests { get; } = [];

        public Func<Task>? WhileNoting { get; set; }

        public string Facts { get; set; } = "Facts:\n- Aaron's dog is Biscuit.";

        public async Task<AgentReply> ReplyAsync(AgentRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            if (!request.Instructions.Contains("should remember later"))
            {
                return AgentReply.Success("Sure.");
            }

            if (WhileNoting is not null)
            {
                await WhileNoting();
            }

            return AgentReply.Success(Facts);
        }
    }
}
