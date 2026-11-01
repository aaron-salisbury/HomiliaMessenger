using Dapper;
using Microsoft.Data.Sqlite;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Database.Migrations;

internal sealed class AddConversationMemoryMigration : Migration
{
    internal override long Version => 4;
    internal override string Description => "Add conversation memory notes";

    protected override Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            ALTER TABLE Conversation ADD COLUMN MemoryNotes TEXT;
            ALTER TABLE Conversation ADD COLUMN MemoryThroughMessageId TEXT;
            """;

        return connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: cancellationToken));
    }
}
