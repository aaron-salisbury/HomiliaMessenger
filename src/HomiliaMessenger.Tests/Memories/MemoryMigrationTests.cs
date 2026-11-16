using Dapper;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.Memories;
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

namespace HomiliaMessenger.Tests.Memories;

[TestClass]
public sealed class MemoryMigrationTests
{
    private const string CONTACT_ID = "11111111-1111-1111-1111-111111111111";
    private const string CONVERSATION_ID = "22222222-2222-2222-2222-222222222222";
    private const string MESSAGE_ID = "33333333-3333-3333-3333-333333333333";
    private const string NOTES = "- Aaron's dog is Biscuit.\n\n* Jen is moving to Portland.\n- Aaron's dog is Biscuit.\nAaron corrected this himself.";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void NoteLines_SplitsTheNotesIntoOneMemoryPerLine()
    {
        Assert.AreSequenceEqual(
            new[] { "Aaron's dog is Biscuit.", "Jen is moving to Portland.", "Aaron corrected this himself." },
            AddContactMemoriesMigration.NoteLines(NOTES).ToArray());
        Assert.IsEmpty(AddContactMemoriesMigration.NoteLines("  \n "));
        Assert.IsEmpty(AddContactMemoriesMigration.NoteLines(null));
    }

    [TestMethod]
    public void NoteLines_SplitsALongLineIntoMemoriesThatFit()
    {
        string notes = string.Concat(Enumerable.Range(1, 20).Select(number => $"Fact {number}: Aaron's sister Jen is moving to Portland with her kids and the dog. "))[..1000];

        IReadOnlyList<string> lines = AddContactMemoriesMigration.NoteLines(notes);

        Assert.IsGreaterThan(1, lines.Count);
        Assert.IsTrue(lines.All(line => line.Length <= Memory.MAX_TEXT_LENGTH));
        Assert.IsTrue(lines.Take(lines.Count - 1).All(line => line.EndsWith('.')));
        Assert.AreEqual(notes.Replace(" ", string.Empty, StringComparison.Ordinal), string.Concat(lines).Replace(" ", string.Empty, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Migration_ImportsTheOldNotesOnceAndKeepsTheCheckpoint()
    {
        string directory = NewDirectory();

        try
        {
            ApplicationDatabase database = new(Path.Combine(directory, "test.db"));
            await new ApplicationDatabaseInitializer(database, LegacyMigrations()).InitializeAsync(TestContext.CancellationToken);
            await SeedLegacyConversationAsync(database);

            await new ApplicationDatabaseInitializer(database, AllMigrations()).InitializeAsync(TestContext.CancellationToken);
            await new ApplicationDatabaseInitializer(database, AllMigrations()).InitializeAsync(TestContext.CancellationToken);
            ClearPool(database);

            using ServiceProvider services = OpenServices(directory);
            IReadOnlyList<Memory> memories = await services.GetRequiredService<IMemoryStore>().ListAsync(new ContactId(Guid.Parse(CONTACT_ID)), TestContext.CancellationToken);
            Conversation? conversation = await services.GetRequiredService<IConversationStore>().GetAsync(new ConversationId(Guid.Parse(CONVERSATION_ID)), TestContext.CancellationToken);
            IReadOnlyList<ChatMessage> messages = await services.GetRequiredService<IConversationStore>().ListMessagesAsync(new ConversationId(Guid.Parse(CONVERSATION_ID)), TestContext.CancellationToken);

            Assert.AreSequenceEqual(
                new[] { "Aaron's dog is Biscuit.", "Jen is moving to Portland.", "Aaron corrected this himself." },
                memories.Select(memory => memory.Text).ToArray());
            Assert.IsTrue(memories.All(memory => memory.Origin == MemoryOrigin.Imported && memory.EmbeddingModel is null));
            Assert.AreEqual(new MessageId(Guid.Parse(MESSAGE_ID)), conversation!.MemoryThrough);
            Assert.HasCount(1, messages);

            // The old notes are left as they were.
            await using (SqliteConnection connection = await database.OpenConnectionAsync(TestContext.CancellationToken))
            {
                Assert.AreEqual(NOTES, await connection.ExecuteScalarAsync<string>("SELECT MemoryNotes FROM Conversation"));
            }

            ClearPool(database);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task Migration_LeavesNothingBehindWhenItFailsAndCompletesOnTheNextStart()
    {
        string directory = NewDirectory();

        try
        {
            ApplicationDatabase database = new(Path.Combine(directory, "test.db"));
            await new ApplicationDatabaseInitializer(database, LegacyMigrations()).InitializeAsync(TestContext.CancellationToken);
            await SeedLegacyConversationAsync(database);

            // A table in the way makes the migration fail partway through.
            await ExecuteAsync(database, "CREATE TABLE MemoryEmbedding (Blocker INTEGER)");
            await Assert.ThrowsAsync<SqliteException>(() => new ApplicationDatabaseInitializer(database, AllMigrations()).InitializeAsync(TestContext.CancellationToken));

            await using (SqliteConnection connection = await database.OpenConnectionAsync(TestContext.CancellationToken))
            {
                Assert.AreEqual(0L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'Memory'"));
                Assert.AreEqual(5L, await connection.ExecuteScalarAsync<long>("SELECT MAX(Version) FROM SchemaMigration"));
            }

            await ExecuteAsync(database, "DROP TABLE MemoryEmbedding");
            await new ApplicationDatabaseInitializer(database, AllMigrations()).InitializeAsync(TestContext.CancellationToken);

            await using (SqliteConnection connection = await database.OpenConnectionAsync(TestContext.CancellationToken))
            {
                Assert.AreEqual(3L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Memory"));
            }

            ClearPool(database);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IEnumerable<Migration> LegacyMigrations()
    {
        return [new CreateContactsMigration(), new CreateConversationsMigration(), new AddRelationshipsAndUserProfileMigration(), new AddConversationMemoryMigration(), new AddContactVoiceMigration()];
    }

    private static IEnumerable<Migration> AllMigrations()
    {
        return [.. LegacyMigrations(), new AddContactMemoriesMigration(), new AddWebSearchMigration(), new AddGroupConversationsMigration()];
    }

    private async Task SeedLegacyConversationAsync(ApplicationDatabase database)
    {
        await using SqliteConnection connection = await database.OpenConnectionAsync(TestContext.CancellationToken);

        await connection.ExecuteAsync("INSERT INTO Contact (Id, GivenName, IsFavorite) VALUES (@Id, 'Walt', 0)", new { Id = CONTACT_ID });
        await connection.ExecuteAsync(
            "INSERT INTO Conversation (Id, ContactId, StartedAtUtc, MemoryNotes, MemoryThroughMessageId) VALUES (@Id, @ContactId, '2026-01-01T00:00:00.0000000+00:00', @Notes, @Through)",
            new { Id = CONVERSATION_ID, ContactId = CONTACT_ID, Notes = NOTES, Through = MESSAGE_ID });
        await connection.ExecuteAsync(
            "INSERT INTO ChatMessage (Id, ConversationId, Sender, Text, SentAtUtc) VALUES (@Id, @ConversationId, 0, 'Hello', '2026-01-01T00:00:00.0000000+00:00')",
            new { Id = MESSAGE_ID, ConversationId = CONVERSATION_ID });
    }

    private async Task ExecuteAsync(ApplicationDatabase database, string sql)
    {
        await using SqliteConnection connection = await database.OpenConnectionAsync(TestContext.CancellationToken);
        await connection.ExecuteAsync(sql);
    }

    private static ServiceProvider OpenServices(string directory)
    {
        ServiceCollection services = new();
        services.AddSingleton<IApplicationDatabase>(new ApplicationDatabase(Path.Combine(directory, "test.db")));
        services.AddScoped<IMemoryStore, Data.Modules.Memories.SqliteMemoryStore>();
        services.AddScoped<IConversationStore, Data.Modules.Conversations.SqliteConversationStore>();

        return services.BuildServiceProvider();
    }

    private static void ClearPool(ApplicationDatabase database)
    {
        using SqliteConnection connection = database.OpenConnectionAsync().GetAwaiter().GetResult();
        SqliteConnection.ClearPool(connection);
    }

    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"HomiliaMessenger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        return directory;
    }
}
