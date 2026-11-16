using Dapper;
using Microsoft.Data.Sqlite;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Database.Migrations;

internal abstract class Migration
{
    internal abstract long Version { get; }

    internal abstract string Description { get; }

    /// <summary>
    /// Whether the migration copies a table that others reference, which SQLite only allows with foreign keys off (or
    /// dropping the old table would delete the rows that reference it). Foreign keys are then checked before committing.
    /// </summary>
    internal virtual bool RebuildsTables => false;

    internal async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!RebuildsTables)
        {
            await ApplyInTransactionAsync(connection, cancellationToken);
            return;
        }

        // Foreign keys cannot be switched off within a transaction.
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA foreign_keys = OFF;", cancellationToken: cancellationToken));

        try
        {
            await ApplyInTransactionAsync(connection, cancellationToken);
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition("PRAGMA foreign_keys = ON;", cancellationToken: CancellationToken.None));
        }
    }

    private async Task ApplyInTransactionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction = connection.BeginTransaction();

        try
        {
            await ExecuteAsync(connection, transaction, cancellationToken);

            if (RebuildsTables)
            {
                long violations = (await connection.QueryAsync(new CommandDefinition("PRAGMA foreign_key_check;", transaction: transaction, cancellationToken: cancellationToken))).LongCount();

                if (violations > 0)
                {
                    throw new InvalidOperationException($"Migration {Version} would leave {violations} broken references.");
                }
            }

            const string recordMigrationSql = """
                INSERT INTO SchemaMigration (Version, Description, AppliedAtUtc)
                VALUES (@Version, @Description, CURRENT_TIMESTAMP);
                """;

            CommandDefinition command = new(
                recordMigrationSql,
                new { Version, Description },
                transaction,
                cancellationToken: cancellationToken);

            await connection.ExecuteAsync(command);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    protected abstract Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken);
}
