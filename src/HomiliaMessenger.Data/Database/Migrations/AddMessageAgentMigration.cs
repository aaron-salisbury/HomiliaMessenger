using Dapper;
using Microsoft.Data.Sqlite;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Database.Migrations;

internal sealed class AddMessageAgentMigration : Migration
{
    internal override long Version => 3;
    internal override string Description => "Record the agent behind each reply";

    protected override Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            ALTER TABLE ChatMessage ADD COLUMN AgentProvider TEXT;
            ALTER TABLE ChatMessage ADD COLUMN AgentModel TEXT;
            """;

        return connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: cancellationToken));
    }
}
