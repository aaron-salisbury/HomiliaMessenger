using Dapper;
using Microsoft.Data.Sqlite;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Database.Migrations;

/// <summary>
/// Repairs databases that recorded the web search migration before PausedForQuota was present in their table.
/// The original migration remains unchanged for new installations.
/// </summary>
internal sealed class RepairWebSearchSettingsMigration : Migration
{
    internal override long Version => 9;
    internal override string Description => "Repair missing web search quota state column";

    protected override async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        long columnExists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM pragma_table_info('WebSearchSettings') WHERE name = 'PausedForQuota'",
            transaction: transaction,
            cancellationToken: cancellationToken));

        if (columnExists == 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "ALTER TABLE WebSearchSettings ADD COLUMN PausedForQuota INTEGER NOT NULL DEFAULT 0",
                transaction: transaction,
                cancellationToken: cancellationToken));
        }
    }
}
