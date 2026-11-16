using Dapper;
using Microsoft.Data.Sqlite;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Database.Migrations;

/// <summary>
/// Stores web search's settings and provider state (one row, off by default), the Tavily credits counted per billing
/// period, cached results, and, with each reply, what was looked up for it.
/// </summary>
/// <remarks>
/// The API key is not stored here: it is kept in the operating system's credential store. Cached results hold only a
/// query and public results, never anything from a conversation, so they are shared by all contacts.
/// </remarks>
internal sealed class AddWebSearchMigration : Migration
{
    internal override long Version => 7;
    internal override string Description => "Add web search settings, credit usage, result cache, and reply sources";

    protected override async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE WebSearchSettings (
                Id INTEGER NOT NULL PRIMARY KEY CHECK(Id = 1),
                Enabled INTEGER NOT NULL,
                TavilyEnabled INTEGER NOT NULL,
                MonthlyCreditBudget INTEGER NOT NULL CHECK(MonthlyCreditBudget >= 0),
                CreditResetDay INTEGER NOT NULL CHECK(CreditResetDay BETWEEN 1 AND 28),
                KeyRejected INTEGER NOT NULL DEFAULT 0,
                PausedUntilUtc TEXT,
                PausedForQuota INTEGER NOT NULL DEFAULT 0,
                Problem TEXT
            );
            INSERT INTO WebSearchSettings (Id, Enabled, TavilyEnabled, MonthlyCreditBudget, CreditResetDay)
            VALUES (1, 0, 1, 500, 1);
            CREATE TABLE WebSearchUsage (
                Provider TEXT NOT NULL,
                PeriodStart TEXT NOT NULL,
                Credits INTEGER NOT NULL CHECK(Credits >= 0),
                PRIMARY KEY (Provider, PeriodStart)
            );
            CREATE TABLE WebSearchCache (
                CacheKey TEXT NOT NULL PRIMARY KEY,
                Results TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                ExpiresAtUtc TEXT NOT NULL
            );
            CREATE INDEX IX_WebSearchCache_Expires ON WebSearchCache(ExpiresAtUtc);
            ALTER TABLE ChatMessage ADD COLUMN WebSearch TEXT;
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: cancellationToken));
    }
}
