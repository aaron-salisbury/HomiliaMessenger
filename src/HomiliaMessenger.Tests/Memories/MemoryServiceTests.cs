using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.Memories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Memories;

[TestClass]
public sealed class MemoryServiceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AddAsync_SavesTheUsersMemoryUnlessBlankTooLongRepeatedOrTheChatIsGone()
    {
        using TestDatabase database = await CreateDatabaseAsync(new FakeEmbedder());
        IMemoryService memories = database.Services.GetRequiredService<IMemoryService>();
        (Contact walt, ConversationId chat) = await MemoryStoreTests.StartChatAsync(database, "Walt");

        MemorySaveResult saved = await memories.AddAsync(chat, "  - Aaron is a nurse.\n", TestContext.CancellationToken);
        MemorySaveResult repeated = await memories.AddAsync(chat, "aaron is a nurse", TestContext.CancellationToken);
        MemorySaveResult blank = await memories.AddAsync(chat, "   ", TestContext.CancellationToken);
        MemorySaveResult tooLong = await memories.AddAsync(chat, new string('x', Memory.MAX_TEXT_LENGTH + 1), TestContext.CancellationToken);
        MemorySaveResult missing = await memories.AddAsync(ConversationId.New(), "Aaron likes jazz.", TestContext.CancellationToken);
        IReadOnlyList<Memory> stored = await memories.ListAsync(walt.Id, TestContext.CancellationToken);

        Assert.IsTrue(saved.Validation.IsValid);
        Assert.AreEqual("Aaron is a nurse.", saved.Memory!.Text);
        Assert.AreEqual(MemoryOrigin.Manual, saved.Memory.Origin);
        Assert.IsFalse(repeated.Validation.IsValid);
        Assert.IsFalse(blank.Validation.IsValid);
        Assert.IsFalse(tooLong.Validation.IsValid);
        Assert.IsFalse(missing.Validation.IsValid);
        Assert.HasCount(1, stored);
    }

    [TestMethod]
    public async Task UpdateAsync_MakesTheMemoryTheUsersAndWaitsForANewVector()
    {
        FakeEmbedder embedder = new();
        using TestDatabase database = await CreateDatabaseAsync(embedder);
        IMemoryService memories = database.Services.GetRequiredService<IMemoryService>();
        IMemoryStore store = database.Services.GetRequiredService<IMemoryStore>();
        (Contact walt, ConversationId chat) = await MemoryStoreTests.StartChatAsync(database, "Walt");
        Memory learned = MemoryStoreTests.NewMemory(walt, chat, "Aaron's dog is Biscuit.") with { Origin = MemoryOrigin.Extracted };
        await store.AddAsync(learned, TestContext.CancellationToken);
        await memories.EmbedPendingAsync(walt.Id, TestContext.CancellationToken);

        MemorySaveResult corrected = await memories.UpdateAsync(learned.Id, "Aaron's dog is Pepper.", learned.Text, TestContext.CancellationToken);
        Memory? pending = await store.GetAsync(learned.Id, TestContext.CancellationToken);
        int embedded = await memories.EmbedPendingAsync(walt.Id, TestContext.CancellationToken);
        Memory? stored = await store.GetAsync(learned.Id, TestContext.CancellationToken);

        Assert.IsTrue(corrected.Validation.IsValid);
        Assert.AreEqual(MemoryOrigin.Manual, corrected.Memory!.Origin);
        Assert.IsNull(pending!.EmbeddingModel);
        Assert.AreEqual(1, embedded);
        Assert.AreEqual(FakeEmbedder.MODEL, stored!.EmbeddingModel);
        Assert.AreEqual("Aaron's dog is Pepper.", embedder.Texts[^1]);
    }

    [TestMethod]
    public async Task UpdateAsync_RefusesAnEditOfAMemoryChangedMeanwhile()
    {
        using TestDatabase database = await CreateDatabaseAsync(new FakeEmbedder());
        IMemoryService memories = database.Services.GetRequiredService<IMemoryService>();
        (Contact walt, ConversationId chat) = await MemoryStoreTests.StartChatAsync(database, "Walt");
        Memory memory = (await memories.AddAsync(chat, "Aaron is a nurse.", TestContext.CancellationToken)).Memory!;

        MemorySaveResult first = await memories.UpdateAsync(memory.Id, "Aaron is a nurse in Denver.", memory.Text, TestContext.CancellationToken);
        MemorySaveResult stale = await memories.UpdateAsync(memory.Id, "Aaron is a doctor.", memory.Text, TestContext.CancellationToken);
        MemorySaveResult blank = await memories.UpdateAsync(memory.Id, " ", first.Memory!.Text, TestContext.CancellationToken);
        bool deleted = await memories.DeleteAsync(memory.Id, TestContext.CancellationToken);
        MemorySaveResult gone = await memories.UpdateAsync(memory.Id, "Aaron is a doctor.", first.Memory.Text, TestContext.CancellationToken);

        Assert.IsTrue(first.Validation.IsValid);
        Assert.IsFalse(stale.Validation.IsValid);
        Assert.IsFalse(blank.Validation.IsValid);
        Assert.IsTrue(deleted);
        Assert.IsFalse(gone.Validation.IsValid);
        Assert.IsEmpty(await memories.ListAsync(walt.Id, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task EmbedPendingAsync_LeavesMemoriesWaitingWhileEmbeddingFailsAndCatchesUpLater()
    {
        FakeEmbedder embedder = new();
        using TestDatabase database = await CreateDatabaseAsync(embedder);
        IMemoryService memories = database.Services.GetRequiredService<IMemoryService>();
        (Contact walt, ConversationId chat) = await MemoryStoreTests.StartChatAsync(database, "Walt");
        await memories.AddAsync(chat, "Aaron is a nurse.", TestContext.CancellationToken);

        embedder.Result = EmbeddingResult.Failed("The model could not run.");
        int failed = await memories.EmbedPendingAsync(walt.Id, TestContext.CancellationToken);
        embedder.Result = null;
        embedder.Dimensions = 0;
        int empty = await memories.EmbedPendingAsync(walt.Id, TestContext.CancellationToken);
        embedder.Model = null;
        int unavailable = await memories.EmbedPendingAsync(walt.Id, TestContext.CancellationToken);
        embedder.Model = FakeEmbedder.MODEL;
        embedder.Dimensions = FakeEmbedder.DIMENSIONS;
        int recovered = await memories.EmbedPendingAsync(walt.Id, TestContext.CancellationToken);

        Assert.AreEqual(0, failed);
        Assert.AreEqual(0, empty);
        Assert.AreEqual(0, unavailable);
        Assert.AreEqual(1, recovered);
        Assert.AreEqual(FakeEmbedder.MODEL, (await memories.ListAsync(walt.Id, TestContext.CancellationToken))[0].EmbeddingModel);
    }

    [TestMethod]
    public async Task EmbedPendingAsync_NeverStoresVectorsFromAModelOtherThanTheOneInUse()
    {
        FakeEmbedder embedder = new();
        using TestDatabase database = await CreateDatabaseAsync(embedder);
        IMemoryService memories = database.Services.GetRequiredService<IMemoryService>();
        (Contact walt, ConversationId chat) = await MemoryStoreTests.StartChatAsync(database, "Walt");
        await memories.AddAsync(chat, "Aaron is a nurse.", TestContext.CancellationToken);
        embedder.Result = EmbeddingResult.Success("switched-meanwhile", [new float[] { 1, 2, 3 }]);

        int embedded = await memories.EmbedPendingAsync(walt.Id, TestContext.CancellationToken);

        Assert.AreEqual(0, embedded);
        Assert.IsNull((await memories.ListAsync(walt.Id, TestContext.CancellationToken))[0].EmbeddingModel);
    }

    [TestMethod]
    public async Task EmbedPendingAsync_EmbedsEveryContactsMemoriesAgainAfterTheModelChanges()
    {
        FakeEmbedder embedder = new();
        using TestDatabase database = await CreateDatabaseAsync(embedder);
        IMemoryService memories = database.Services.GetRequiredService<IMemoryService>();
        (Contact walt, ConversationId waltChat) = await MemoryStoreTests.StartChatAsync(database, "Walt");
        (Contact rose, ConversationId roseChat) = await MemoryStoreTests.StartChatAsync(database, "Rose");

        for (int index = 0; index < MemoryService.EMBED_BATCH_SIZE + 5; index++)
        {
            await memories.AddAsync(waltChat, $"Walt memory {index}.", TestContext.CancellationToken);
        }

        await memories.AddAsync(roseChat, "Rose memory.", TestContext.CancellationToken);
        int first = await memories.EmbedPendingAsync(null, TestContext.CancellationToken);
        embedder.Model = "bigger-model";
        embedder.Dimensions = 512;
        int again = await memories.EmbedPendingAsync(null, TestContext.CancellationToken);
        IReadOnlyList<Memory> all = [.. await memories.ListAsync(walt.Id, TestContext.CancellationToken), .. await memories.ListAsync(rose.Id, TestContext.CancellationToken)];

        Assert.AreEqual(MemoryService.EMBED_BATCH_SIZE + 6, first);
        Assert.AreEqual(MemoryService.EMBED_BATCH_SIZE + 6, again);
        Assert.IsTrue(all.All(memory => memory.EmbeddingModel == "bigger-model" && memory.EmbeddingDimensions == 512));
        Assert.AreEqual("Walt memory 0.", all[0].Text);
    }

    [TestMethod]
    public async Task EmbedPendingAsync_StopsWhenCanceled()
    {
        FakeEmbedder embedder = new();
        using TestDatabase database = await CreateDatabaseAsync(embedder);
        IMemoryService memories = database.Services.GetRequiredService<IMemoryService>();
        (Contact walt, ConversationId chat) = await MemoryStoreTests.StartChatAsync(database, "Walt");
        await memories.AddAsync(chat, "Aaron is a nurse.", TestContext.CancellationToken);
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => memories.EmbedPendingAsync(walt.Id, cancel.Token));
        Assert.AreEqual(1, await memories.EmbedPendingAsync(walt.Id, TestContext.CancellationToken));
    }

    [TestMethod]
    public void Validate_RefusesVectorsThatCannotBeCompared()
    {
        Assert.IsTrue(EmbeddingVectors.AreUsable(EmbeddingResult.Success("m", [[1, 0], [0, 1]]), 2));
        Assert.IsFalse(EmbeddingVectors.AreUsable(EmbeddingResult.Success("m", [[1, 0], [0, 1, 0]]), 2));
        Assert.IsFalse(EmbeddingVectors.AreUsable(EmbeddingResult.Success("m", [[0, 0]]), 1));
        Assert.IsFalse(EmbeddingVectors.AreUsable(EmbeddingResult.Success("m", [[float.PositiveInfinity, 1]]), 1));
        Assert.IsFalse(EmbeddingVectors.AreUsable(EmbeddingResult.Success(" ", [[1, 0]]), 1));
        Assert.IsFalse(EmbeddingVectors.AreUsable(EmbeddingResult.Unavailable("No model."), 0));
        Assert.AreEqual(1f, VectorSimilarity.Cosine([1, 2, 3], [2, 4, 6]), 1e-6f);
        Assert.AreEqual(0f, VectorSimilarity.Cosine([1, 0], [0, 1]), 1e-6f);
        Assert.ThrowsExactly<ArgumentException>(() => VectorSimilarity.Cosine([1, 0], [1, 0, 0]));
    }

    private Task<TestDatabase> CreateDatabaseAsync(FakeEmbedder embedder)
    {
        return TestDatabase.CreateAsync(TestContext, services =>
        {
            services.AddSingleton<IConversationAgent>(new SimulatedAgent(new Random(7), TimeProvider.System, TimeSpan.Zero, TimeSpan.Zero));
            services.AddSingleton<ITextEmbedder>(embedder);
        });
    }
}
