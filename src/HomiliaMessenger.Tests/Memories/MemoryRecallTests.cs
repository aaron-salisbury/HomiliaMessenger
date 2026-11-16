using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.Memories;
using HomiliaMessenger.Business.Modules.Profile;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Memories;

[TestClass]
public sealed class MemoryRecallTests
{
    private const string JEN = "Aaron's sister Jen lives in Portland.";
    private const string JEN_QUERY = "How is Jen settling into Portland?";
    private static readonly DateTimeOffset START = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RecallAsync_GivesEveryMemoryInOrderWhenTheyAllFit()
    {
        FakeEmbedder embedder = new();
        using TestDatabase database = await CreateDatabaseAsync(embedder);
        (Contact walt, ConversationId chat) = await MemoryStoreTests.StartChatAsync(database, "Walt");
        await AddAsync(database, walt, chat, ["Aaron's dog is Biscuit.", "Aaron is a nurse.", "Jen is moving in March."]);

        IReadOnlyList<Memory> recalled = await Recall(database).RecallAsync(walt.Id, "anything at all", TestContext.CancellationToken);

        Assert.AreSequenceEqual(new[] { "Aaron's dog is Biscuit.", "Aaron is a nurse.", "Jen is moving in March." }, recalled.Select(memory => memory.Text).ToArray());
        Assert.IsEmpty(embedder.Texts);
    }

    [TestMethod]
    public async Task RecallAsync_FindsAnOldRelevantMemoryOnceTheyNoLongerFit()
    {
        FakeEmbedder embedder = new();
        using TestDatabase database = await CreateDatabaseAsync(embedder);
        (Contact walt, ConversationId chat) = await MemoryStoreTests.StartChatAsync(database, "Walt");
        await AddAsync(database, walt, chat, [JEN, .. Filler(40)]);
        await database.Services.GetRequiredService<IMemoryService>().EmbedPendingAsync(walt.Id, TestContext.CancellationToken);

        IReadOnlyList<Memory> recalled = await Recall(database).RecallAsync(walt.Id, JEN_QUERY, TestContext.CancellationToken);

        Assert.AreEqual(JEN, recalled[0].Text);
        Assert.IsLessThanOrEqualTo(new MemoryRecallOptions().MaxCharacters, recalled.Sum(memory => memory.Text.Length + 1));
        Assert.Contains(EmbeddingPurpose.Query, embedder.Purposes);
        Assert.AreEqual(Filler(40)[^1], recalled[^1].Text);
        Assert.IsLessThan(41, recalled.Count);
    }

    [TestMethod]
    public void Select_LeavesOutMemoriesBelowTheSimilarityThresholdAndKeepsTheBudget()
    {
        FakeEmbedder embedder = new();
        List<Memory> memories = [.. Memories([JEN, .. Filler(40)]).Select(memory => memory with { EmbeddingModel = FakeEmbedder.MODEL })];
        float[] query = embedder.Vector(JEN_QUERY);
        List<ScoredMemory> similar = [.. memories.Select(memory => new ScoredMemory(memory, VectorSimilarity.Cosine(query, embedder.Vector(memory.Text)), embedder.Vector(memory.Text)))];

        IReadOnlyList<Memory> strict = MemoryRecall.Select(memories, similar, FakeEmbedder.MODEL, JEN_QUERY, new MemoryRecallOptions { MinSimilarity = 0.99f, RecentCharacters = 0 });
        IReadOnlyList<Memory> usual = MemoryRecall.Select(memories, similar, FakeEmbedder.MODEL, JEN_QUERY, new MemoryRecallOptions { MinSimilarity = 0.3f, RecentCharacters = 0 });
        IReadOnlyList<Memory> small = MemoryRecall.Select(memories, similar, FakeEmbedder.MODEL, JEN_QUERY, new MemoryRecallOptions { MaxCharacters = 20 });

        Assert.IsEmpty(strict);
        Assert.AreSequenceEqual(new[] { JEN }, usual.Select(memory => memory.Text).ToArray());
        Assert.IsTrue(small.All(memory => memory.Text.Length + 1 <= 20));
    }

    [TestMethod]
    public async Task RecallAsync_UsesTheThresholdTheEmbeddingModelReports()
    {
        FakeEmbedder embedder = new();
        using TestDatabase database = await CreateDatabaseAsync(embedder);
        (Contact walt, ConversationId chat) = await MemoryStoreTests.StartChatAsync(database, "Walt");
        await AddAsync(database, walt, chat, [JEN, .. Filler(40)]);
        await database.Services.GetRequiredService<IMemoryService>().EmbedPendingAsync(walt.Id, TestContext.CancellationToken);

        IReadOnlyList<Memory> lenient = await Recall(database).RecallAsync(walt.Id, JEN_QUERY, TestContext.CancellationToken);
        embedder.MinSimilarity = 0.9f;
        IReadOnlyList<Memory> strict = await Recall(database).RecallAsync(walt.Id, JEN_QUERY, TestContext.CancellationToken);

        Assert.AreEqual(JEN, lenient[0].Text);
        Assert.DoesNotContain(memory => memory.Text == JEN, strict);
    }

    [TestMethod]
    public void QueryFor_SearchesWithTheUsersLatestMessagesEvenAfterTheContactReplied()
    {
        List<ChatMessage> messages = [Message(MessageSender.User, "Old news."), Message(MessageSender.Contact, "Huh."), Message(MessageSender.User, "How is Jen settling in?"), Message(MessageSender.User, "And Portland?")];

        string latest = MemoryRecall.QueryFor(messages, 1000);
        messages.Add(Message(MessageSender.Contact, "Good, I hear."));
        string afterReply = MemoryRecall.QueryFor(messages, 1000);

        Assert.AreEqual("How is Jen settling in?\nAnd Portland?", latest);
        Assert.AreEqual(latest, afterReply);
        Assert.AreEqual("Portland?", MemoryRecall.QueryFor(messages, 9));
        Assert.AreEqual(string.Empty, MemoryRecall.QueryFor([], 1000));
    }

    [TestMethod]
    public void QueryFor_SearchesAFollowUpWithWhatItFollowsUpOn()
    {
        List<ChatMessage> messages =
        [
            Message(MessageSender.User, "Old news."),
            Message(MessageSender.Contact, "Huh."),
            Message(MessageSender.User, "Is Biscuit still chewing the couch?"),
            Message(MessageSender.Contact, "He got the cushions too."),
            Message(MessageSender.User, "Tell me more!")
        ];

        string query = MemoryRecall.QueryFor(messages, 1000);

        Assert.AreEqual("Is Biscuit still chewing the couch?\nTell me more!", query);
        Assert.AreEqual("Tell me more!", MemoryRecall.QueryFor([Message(MessageSender.User, "Tell me more!")], 1000));
    }

    [TestMethod]
    public void Select_GivesOnlyOneOfMemoriesThatSayTheSameThing()
    {
        FakeEmbedder embedder = new();
        List<Memory> memories = [.. Memories(["Aaron's dog is Biscuit.", "Biscuit is Aaron's dog!", "aaron's dog is biscuit"]).Select(memory => memory with { EmbeddingModel = FakeEmbedder.MODEL })];
        float[] query = embedder.Vector("Biscuit the dog");
        List<ScoredMemory> similar = [.. memories.Select(memory => new ScoredMemory(memory, VectorSimilarity.Cosine(query, embedder.Vector(memory.Text)), embedder.Vector(memory.Text)))];

        IReadOnlyList<Memory> recalled = MemoryRecall.Select(memories, similar, FakeEmbedder.MODEL, "Biscuit the dog", new MemoryRecallOptions { RecentCharacters = 0 });

        Assert.HasCount(1, recalled);
    }

    [TestMethod]
    public void Select_GivesBothSidesOfAContradiction()
    {
        FakeEmbedder embedder = new();
        List<Memory> memories = [.. Memories(["Aaron starts work on Monday.", "Aaron starts work on Tuesday."]).Select(memory => memory with { EmbeddingModel = FakeEmbedder.MODEL })];
        float[] query = embedder.Vector("When does Aaron start work?");
        List<ScoredMemory> similar = [.. memories.Select(memory => new ScoredMemory(memory, VectorSimilarity.Cosine(query, embedder.Vector(memory.Text)), embedder.Vector(memory.Text)))];

        IReadOnlyList<Memory> recalled = MemoryRecall.Select(memories, similar, FakeEmbedder.MODEL, "When does Aaron start work?", new MemoryRecallOptions { RecentCharacters = 0 });

        Assert.HasCount(2, recalled);
    }

    [TestMethod]
    [DataRow("unavailable")]
    [DataRow("throws")]
    [DataRow("wrong count")]
    [DataRow("not a number")]
    [DataRow("other model")]
    public async Task RecallAsync_FallsBackToSharedWordsWhenEmbeddingCannotHelp(string failure)
    {
        FakeEmbedder embedder = new();
        using TestDatabase database = await CreateDatabaseAsync(embedder);
        (Contact walt, ConversationId chat) = await MemoryStoreTests.StartChatAsync(database, "Walt");
        await AddAsync(database, walt, chat, [JEN, .. Filler(40)]);
        await database.Services.GetRequiredService<IMemoryService>().EmbedPendingAsync(walt.Id, TestContext.CancellationToken);

        switch (failure)
        {
            case "unavailable":
                embedder.Model = null;
                break;
            case "throws":
                embedder.Error = new InvalidOperationException("The model crashed.");
                break;
            case "wrong count":
                embedder.Result = EmbeddingResult.Success(FakeEmbedder.MODEL, []);
                break;
            case "not a number":
                embedder.Result = EmbeddingResult.Success(FakeEmbedder.MODEL, [[float.NaN, 1]]);
                break;
            case "other model":
                // The model changed: no stored vector is comparable, so every memory is matched by words.
                embedder.Model = "another-model";
                break;
        }

        IReadOnlyList<Memory> recalled = await Recall(database).RecallAsync(walt.Id, JEN_QUERY, TestContext.CancellationToken);

        Assert.AreEqual(JEN, recalled[0].Text);
    }

    [TestMethod]
    public async Task RecallAsync_MatchesMemoriesNotYetEmbeddedByTheirWords()
    {
        FakeEmbedder embedder = new();
        using TestDatabase database = await CreateDatabaseAsync(embedder);
        (Contact walt, ConversationId chat) = await MemoryStoreTests.StartChatAsync(database, "Walt");
        await AddAsync(database, walt, chat, Filler(40));
        await database.Services.GetRequiredService<IMemoryService>().EmbedPendingAsync(walt.Id, TestContext.CancellationToken);
        await AddAsync(database, walt, chat, [JEN], START.AddDays(-1));

        IReadOnlyList<Memory> recalled = await Recall(database).RecallAsync(walt.Id, JEN_QUERY, TestContext.CancellationToken);

        Assert.AreEqual(JEN, recalled[0].Text);
        Assert.IsNull(recalled[0].EmbeddingModel);
    }

    [TestMethod]
    public async Task RecallAsync_StopsWhenCanceled()
    {
        FakeEmbedder embedder = new();
        using TestDatabase database = await CreateDatabaseAsync(embedder);
        (Contact walt, ConversationId chat) = await MemoryStoreTests.StartChatAsync(database, "Walt");
        await AddAsync(database, walt, chat, [JEN, .. Filler(40)]);
        using CancellationTokenSource cancel = new();
        embedder.Error = new OperationCanceledException(cancel.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() => Recall(database).RecallAsync(walt.Id, JEN_QUERY, cancel.Token));
    }

    [TestMethod]
    public async Task ReplyAsync_GivesTheAgentTheRecalledMemoriesAndStillRepliesWhenEmbeddingFails()
    {
        RecordingAgent agent = new();
        FakeEmbedder embedder = new();
        using TestDatabase database = await CreateDatabaseAsync(embedder, agent);
        IConversationService conversations = database.Services.GetRequiredService<IConversationService>();
        Contact walt = new() { Id = ContactId.New(), GivenName = "Walt", Birthday = new DateOnly(1948, 3, 14), Agent = new AgentAssignment(RecordingAgent.PROVIDER, "m") };
        await database.Services.GetRequiredService<IContactService>().SaveAsync(walt, TestContext.CancellationToken);
        await database.Services.GetRequiredService<IUserProfileService>().SaveAsync(new UserProfile { Name = "Aaron" }, TestContext.CancellationToken);
        SendMessageResult sent = await conversations.SendAsync(walt.Id, "Hello", TestContext.CancellationToken);
        await AddAsync(database, walt, sent.Conversation!.Id, [JEN, .. Filler(40)]);
        await database.Services.GetRequiredService<IMemoryService>().EmbedPendingAsync(walt.Id, TestContext.CancellationToken);

        await conversations.SendAsync(walt.Id, JEN_QUERY, TestContext.CancellationToken);
        ConversationReply first = await conversations.ReplyAsync(sent.Conversation.Id, cancellationToken: TestContext.CancellationToken);
        embedder.Error = new InvalidOperationException("The model crashed.");
        ConversationReply second = await conversations.ReplyAsync(sent.Conversation.Id, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(ReplyStatus.Success, first.Status);
        Assert.AreEqual(ReplyStatus.Success, second.Status);
        Assert.Contains("- " + JEN, agent.Requests[0].Instructions);
        Assert.DoesNotContain("Filler fact 0 ", agent.Requests[0].Instructions);
        Assert.Contains("- " + JEN, agent.Requests[1].Instructions);
        Assert.IsLessThan(agent.Requests[0].Instructions.IndexOf("How to reply:", StringComparison.Ordinal), agent.Requests[0].Instructions.IndexOf("What you remember", StringComparison.Ordinal));
    }

    private Task<TestDatabase> CreateDatabaseAsync(FakeEmbedder embedder, IConversationAgent? agent = null)
    {
        return TestDatabase.CreateAsync(TestContext, services =>
        {
            services.AddSingleton<IConversationAgent>(new SimulatedAgent(new Random(7), TimeProvider.System, TimeSpan.Zero, TimeSpan.Zero));
            services.AddSingleton<ITextEmbedder>(embedder);

            if (agent is not null)
            {
                services.AddSingleton(agent);
            }
        });
    }

    private static MemoryRecall Recall(TestDatabase database)
    {
        return database.Services.GetRequiredService<MemoryRecall>();
    }

    private async Task AddAsync(TestDatabase database, Contact contact, ConversationId chat, IReadOnlyList<string> texts, DateTimeOffset? start = null)
    {
        IMemoryStore store = database.Services.GetRequiredService<IMemoryStore>();

        for (int index = 0; index < texts.Count; index++)
        {
            Assert.IsTrue(await store.AddAsync(MemoryStoreTests.NewMemory(contact, chat, texts[index], (start ?? START).AddMinutes(index)), TestContext.CancellationToken));
        }
    }

    private static List<string> Filler(int count)
    {
        return [.. Enumerable.Range(0, count).Select(index => $"Filler fact {index} mentions topic{index} and item{index} only.")];
    }

    private static ChatMessage Message(MessageSender sender, string text)
    {
        return new ChatMessage { Id = MessageId.New(), ConversationId = ConversationId.New(), Sender = sender, Text = text, SentAtUtc = START };
    }

    private static List<Memory> Memories(IReadOnlyList<string> texts)
    {
        return [.. texts.Select((text, index) => new Memory
        {
            Id = MemoryId.New(),
            ContactId = new ContactId(Guid.Empty),
            Text = text,
            CreatedAtUtc = START.AddMinutes(index),
            UpdatedAtUtc = START.AddMinutes(index)
        })];
    }

    private sealed class RecordingAgent : IConversationAgent
    {
        internal const string PROVIDER = "Recording";

        public string Provider => PROVIDER;

        public List<AgentRequest> Requests { get; } = [];

        public Task<AgentReply> ReplyAsync(AgentRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(AgentReply.Success("Sure."));
        }
    }
}
