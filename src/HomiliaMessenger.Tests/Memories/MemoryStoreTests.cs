using Dapper;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.Memories;
using HomiliaMessenger.Data.Database;
using HomiliaMessenger.Tests.Conversations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Memories;

[TestClass]
public sealed class MemoryStoreTests
{
    private static readonly FakeEmbedder EMBEDDER = new();

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Memories_SurviveARestartAndAChangeOfAgent()
    {
        TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext);

        try
        {
            (Contact walt, ConversationId conversation) = await StartChatAsync(database, "Walt");
            IMemoryStore store = database.Services.GetRequiredService<IMemoryStore>();
            Memory memory = NewMemory(walt, conversation, "Aaron's dog is Biscuit.");
            await store.AddAsync(memory, TestContext.CancellationToken);
            await store.SaveEmbeddingAsync(memory.Id, memory.Text, FakeEmbedder.MODEL, EMBEDDER.Vector(memory.Text), TestContext.CancellationToken);
            await database.Services.GetRequiredService<IContactService>().SaveAsync(walt with { Agent = new AgentAssignment("Local", "phi-4-mini") }, TestContext.CancellationToken);

            database = await database.ReopenAsync(TestContext);
            IReadOnlyList<Memory> reopened = await database.Services.GetRequiredService<IMemoryStore>().ListAsync(walt.Id, TestContext.CancellationToken);

            Assert.HasCount(1, reopened);
            Assert.AreEqual(memory.Id, reopened[0].Id);
            Assert.AreEqual("Aaron's dog is Biscuit.", reopened[0].Text);
            Assert.AreEqual(MemoryOrigin.Manual, reopened[0].Origin);
            Assert.AreEqual(memory.CreatedAtUtc, reopened[0].CreatedAtUtc);
            Assert.AreEqual(FakeEmbedder.MODEL, reopened[0].EmbeddingModel);
            Assert.AreEqual(FakeEmbedder.DIMENSIONS, reopened[0].EmbeddingDimensions);
        }
        finally
        {
            database.Dispose();
        }
    }

    [TestMethod]
    public async Task Memories_NeverCrossFromOneContactToAnother()
    {
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext);
        IMemoryStore store = database.Services.GetRequiredService<IMemoryStore>();
        (Contact walt, ConversationId waltChat) = await StartChatAsync(database, "Walt");
        (Contact rose, ConversationId roseChat) = await StartChatAsync(database, "Rose");
        Memory waltMemory = await AddEmbeddedAsync(store, walt, waltChat, "Aaron's dog is Biscuit.");
        Memory roseMemory = await AddEmbeddedAsync(store, rose, roseChat, "Aaron's dog is Biscuit.");

        // A memory claiming another contact still belongs to its conversation's contact.
        Memory misattributed = NewMemory(walt, roseChat, "Aaron is a nurse.");
        await store.AddAsync(misattributed, TestContext.CancellationToken);

        IReadOnlyList<ScoredMemory> waltFound = await store.FindSimilarAsync(walt.Id, FakeEmbedder.MODEL, EMBEDDER.Vector("dog Biscuit"), 10, TestContext.CancellationToken);
        IReadOnlyList<Memory> waltListed = await store.ListAsync(walt.Id, TestContext.CancellationToken);
        IReadOnlyList<Memory> roseListed = await store.ListAsync(rose.Id, TestContext.CancellationToken);
        IReadOnlyList<Memory> waltPending = await store.ListUnembeddedAsync(walt.Id, FakeEmbedder.MODEL, 10, TestContext.CancellationToken);

        Assert.AreSequenceEqual(new[] { waltMemory.Id }, waltFound.Select(found => found.Memory.Id).ToArray());
        Assert.AreSequenceEqual(new[] { waltMemory.Id }, waltListed.Select(memory => memory.Id).ToArray());
        Assert.AreSequenceEqual(new[] { roseMemory.Id, misattributed.Id }, roseListed.Select(memory => memory.Id).ToArray());
        Assert.AreEqual(rose.Id, roseListed[1].ContactId);
        Assert.IsEmpty(waltPending);
    }

    [TestMethod]
    public async Task DeletingAContactOrItsChat_RemovesItsMemoriesAndVectors()
    {
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext);
        IMemoryStore store = database.Services.GetRequiredService<IMemoryStore>();
        (Contact walt, ConversationId waltChat) = await StartChatAsync(database, "Walt");
        (Contact rose, ConversationId roseChat) = await StartChatAsync(database, "Rose");
        (Contact sam, ConversationId samChat) = await StartChatAsync(database, "Sam");
        await AddEmbeddedAsync(store, walt, waltChat, "Aaron's dog is Biscuit.");
        await AddEmbeddedAsync(store, rose, roseChat, "Aaron is a nurse.");
        await AddEmbeddedAsync(store, sam, samChat, "Aaron likes jazz.");

        await database.Services.GetRequiredService<IContactService>().DeleteAsync(walt.Id, TestContext.CancellationToken);
        await database.Services.GetRequiredService<IConversationService>().DeleteAsync(roseChat, TestContext.CancellationToken);

        Assert.IsEmpty(await store.ListAsync(walt.Id, TestContext.CancellationToken));
        Assert.IsEmpty(await store.ListAsync(rose.Id, TestContext.CancellationToken));
        Assert.HasCount(1, await store.ListAsync(sam.Id, TestContext.CancellationToken));
        Assert.AreEqual(1L, await CountAsync(database, "Memory"));
        Assert.AreEqual(1L, await CountAsync(database, "MemoryEmbedding"));
    }

    [TestMethod]
    public async Task FindSimilarAsync_RanksMemoriesByCosineSimilarity()
    {
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext);
        IMemoryStore store = database.Services.GetRequiredService<IMemoryStore>();
        (Contact walt, ConversationId chat) = await StartChatAsync(database, "Walt");
        Memory dog = await AddEmbeddedAsync(store, walt, chat, "Aaron's dog Biscuit loves the beach.");
        Memory job = await AddEmbeddedAsync(store, walt, chat, "Aaron works night shifts as a nurse.");
        Memory beach = await AddEmbeddedAsync(store, walt, chat, "Aaron grew up near the beach.");

        IReadOnlyList<ScoredMemory> found = await store.FindSimilarAsync(walt.Id, FakeEmbedder.MODEL, EMBEDDER.Vector("Biscuit the dog went to the beach"), 2, TestContext.CancellationToken);

        Assert.AreSequenceEqual(new[] { dog.Id, beach.Id }, found.Select(memory => memory.Memory.Id).ToArray());
        Assert.IsGreaterThan(found[1].Score, found[0].Score);
        Assert.DoesNotContain(memory => memory.Memory.Id == job.Id, found);
    }

    [TestMethod]
    public async Task FindSimilarAsync_ComparesOnlyWellFormedVectorsFromTheSameModel()
    {
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext);
        IMemoryStore store = database.Services.GetRequiredService<IMemoryStore>();
        (Contact walt, ConversationId chat) = await StartChatAsync(database, "Walt");
        Memory good = await AddEmbeddedAsync(store, walt, chat, "Aaron's dog is Biscuit.");
        Memory otherModel = NewMemory(walt, chat, "Aaron's dog is a beagle.");
        Memory truncated = NewMemory(walt, chat, "Aaron's dog is old.");
        Memory notANumber = NewMemory(walt, chat, "Aaron's dog is loud.");

        foreach (Memory memory in new[] { otherModel, truncated, notANumber })
        {
            await store.AddAsync(memory, TestContext.CancellationToken);
        }

        await store.SaveEmbeddingAsync(otherModel.Id, otherModel.Text, "another-model", EMBEDDER.Vector(otherModel.Text), TestContext.CancellationToken);
        await InsertRawVectorAsync(database, truncated.Id, FakeEmbedder.DIMENSIONS, new byte[10]);
        float[] withNaN = EMBEDDER.Vector(notANumber.Text);
        withNaN[0] = float.NaN;
        await InsertRawVectorAsync(database, notANumber.Id, FakeEmbedder.DIMENSIONS, Data.Modules.Memories.SqliteMemoryStore.ToBytes(withNaN));

        IReadOnlyList<ScoredMemory> found = await store.FindSimilarAsync(walt.Id, FakeEmbedder.MODEL, EMBEDDER.Vector("Aaron's dog"), 10, TestContext.CancellationToken);
        IReadOnlyList<ScoredMemory> wrongSize = await store.FindSimilarAsync(walt.Id, FakeEmbedder.MODEL, new float[] { 1, 0, 0 }, 10, TestContext.CancellationToken);
        IReadOnlyList<Memory> pendingForOtherModel = await store.ListUnembeddedAsync(walt.Id, FakeEmbedder.MODEL, 10, TestContext.CancellationToken);

        Assert.AreSequenceEqual(new[] { good.Id }, found.Select(memory => memory.Memory.Id).ToArray());
        Assert.IsEmpty(wrongSize);
        Assert.Contains(memory => memory.Id == otherModel.Id, pendingForOtherModel);
        Assert.Contains(memory => memory.Id == truncated.Id, pendingForOtherModel);
        Assert.IsNull((await store.GetAsync(truncated.Id, TestContext.CancellationToken))!.EmbeddingModel);
        Assert.IsNull((await store.GetAsync(notANumber.Id, TestContext.CancellationToken))!.EmbeddingModel);
        IReadOnlyList<Memory> pendingAfterSearch = await store.ListUnembeddedAsync(walt.Id, FakeEmbedder.MODEL, 10, TestContext.CancellationToken);
        Assert.Contains(memory => memory.Id == notANumber.Id, pendingAfterSearch);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveEmbeddingAsync(good.Id, good.Text, FakeEmbedder.MODEL, new float[] { float.PositiveInfinity }, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task UpdateAsync_DiscardsTheOldVectorAndRefusesAStaleEdit()
    {
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext);
        IMemoryStore store = database.Services.GetRequiredService<IMemoryStore>();
        (Contact walt, ConversationId chat) = await StartChatAsync(database, "Walt");
        Memory memory = await AddEmbeddedAsync(store, walt, chat, "Aaron's dog is Biscuit.");

        bool updated = await store.UpdateAsync(memory.Id, "Aaron's dog is Pepper.", MemoryOrigin.Manual, DateTimeOffset.UtcNow, memory.Text, TestContext.CancellationToken);
        bool stale = await store.UpdateAsync(memory.Id, "Aaron's dog is Rex.", MemoryOrigin.Manual, DateTimeOffset.UtcNow, memory.Text, TestContext.CancellationToken);
        bool oldVector = await store.SaveEmbeddingAsync(memory.Id, memory.Text, FakeEmbedder.MODEL, EMBEDDER.Vector(memory.Text), TestContext.CancellationToken);
        Memory? stored = await store.GetAsync(memory.Id, TestContext.CancellationToken);

        Assert.IsTrue(updated);
        Assert.IsFalse(stale);
        Assert.IsFalse(oldVector);
        Assert.AreEqual("Aaron's dog is Pepper.", stored!.Text);
        Assert.IsNull(stored.EmbeddingModel);
    }

    [TestMethod]
    public async Task AddExtractedAsync_SavesTheMemoriesAndCheckpointTogetherOrNotAtAll()
    {
        using TestDatabase database = await ConversationServiceTests.CreateDatabaseAsync(TestContext);
        IMemoryStore store = database.Services.GetRequiredService<IMemoryStore>();
        IConversationService conversations = database.Services.GetRequiredService<IConversationService>();
        (Contact walt, ConversationId chat) = await StartChatAsync(database, "Walt");
        MessageId first = (await conversations.ListMessagesAsync(chat, TestContext.CancellationToken))[0].Id;
        MessageId later = MessageId.New();

        bool saved = await store.AddExtractedAsync(chat, walt.Id, [NewMemory(walt, chat, "One.")], first, null, TestContext.CancellationToken);
        bool stale = await store.AddExtractedAsync(chat, walt.Id, [NewMemory(walt, chat, "Two.")], later, null, TestContext.CancellationToken);
        bool missing = await store.AddExtractedAsync(ConversationId.New(), walt.Id, [NewMemory(walt, chat, "Three.")], later, null, TestContext.CancellationToken);
        Conversation? stored = await conversations.FindByContactAsync(walt.Id, TestContext.CancellationToken);

        Assert.IsTrue(saved);
        Assert.IsFalse(stale);
        Assert.IsFalse(missing);
        Assert.AreEqual(first, stored!.MemoryThrough);
        Assert.AreSequenceEqual(new[] { "One." }, (await store.ListAsync(walt.Id, TestContext.CancellationToken)).Select(memory => memory.Text).ToArray());
    }

    internal static async Task<(Contact Contact, ConversationId Conversation)> StartChatAsync(TestDatabase database, string name)
    {
        Contact contact = new() { Id = ContactId.New(), GivenName = name, Birthday = new DateOnly(1970, 1, 1) };
        await database.Services.GetRequiredService<IContactService>().SaveAsync(contact);
        SendMessageResult sent = await database.Services.GetRequiredService<IConversationService>().SendAsync(contact.Id, "Hello");

        return (contact, sent.Conversation!.Id);
    }

    internal static Memory NewMemory(Contact contact, ConversationId conversation, string text, DateTimeOffset? at = null)
    {
        DateTimeOffset now = at ?? DateTimeOffset.UtcNow;

        return new Memory
        {
            Id = MemoryId.New(),
            ContactId = contact.Id,
            ConversationId = conversation,
            Text = text,
            Origin = MemoryOrigin.Manual,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    private async Task<Memory> AddEmbeddedAsync(IMemoryStore store, Contact contact, ConversationId conversation, string text)
    {
        Memory memory = NewMemory(contact, conversation, text);
        Assert.IsTrue(await store.AddAsync(memory, TestContext.CancellationToken));
        Assert.IsTrue(await store.SaveEmbeddingAsync(memory.Id, text, FakeEmbedder.MODEL, EMBEDDER.Vector(text), TestContext.CancellationToken));

        return memory;
    }

    private async Task<long> CountAsync(TestDatabase database, string table)
    {
        await using SqliteConnection connection = await database.Services.GetRequiredService<IApplicationDatabase>().OpenConnectionAsync(TestContext.CancellationToken);

        return await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}");
    }

    private async Task InsertRawVectorAsync(TestDatabase database, MemoryId id, int dimensions, byte[] bytes)
    {
        await using SqliteConnection connection = await database.Services.GetRequiredService<IApplicationDatabase>().OpenConnectionAsync(TestContext.CancellationToken);

        await connection.ExecuteAsync(
            "INSERT INTO MemoryEmbedding (MemoryId, Model, Dimensions, Vector, CreatedAtUtc) VALUES (@Id, @Model, @Dimensions, @Vector, '2026-01-01T00:00:00.0000000+00:00')",
            new { Id = id.Value.ToString("D"), Model = FakeEmbedder.MODEL, Dimensions = dimensions, Vector = bytes });
    }
}
