using Dapper;
using Microsoft.Data.Sqlite;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Database.Migrations;

internal sealed class CreateConversationsMigration : Migration
{
    internal override long Version => 2;
    internal override string Description => "Create conversation storage";

    protected override Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        // Timestamps are UTC round-trip text, so they sort chronologically as text.
        const string sql = """
            CREATE TABLE Conversation (
                Id TEXT NOT NULL PRIMARY KEY,
                ContactId TEXT NOT NULL UNIQUE REFERENCES Contact(Id) ON DELETE CASCADE,
                StartedAtUtc TEXT NOT NULL
            );
            CREATE TABLE ChatMessage (
                Id TEXT NOT NULL PRIMARY KEY,
                ConversationId TEXT NOT NULL REFERENCES Conversation(Id) ON DELETE CASCADE,
                Sender INTEGER NOT NULL CHECK(Sender IN (0, 1)),
                Text TEXT NOT NULL,
                SentAtUtc TEXT NOT NULL,
                AgentProvider TEXT,
                AgentModel TEXT,
                CHECK((AgentProvider IS NULL) = (AgentModel IS NULL))
            );
            CREATE INDEX IX_ChatMessage_Conversation ON ChatMessage(ConversationId, SentAtUtc);
            """;

        return connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: cancellationToken));
    }
}
