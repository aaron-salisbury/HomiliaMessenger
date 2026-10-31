using Dapper;
using Microsoft.Data.Sqlite;
using HomiliaMessenger.Data.Database.Migrations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Database;

internal sealed class ApplicationDatabaseInitializer
{
    private readonly IApplicationDatabase _database;
    private readonly IReadOnlyList<Migration> _migrations;

    internal ApplicationDatabaseInitializer(IApplicationDatabase database, IEnumerable<Migration> migrations)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(migrations);

        _database = database;
        _migrations = [.. migrations.OrderBy(migration => migration.Version)];

        if (_migrations.Select(migration => migration.Version).Distinct().Count() != _migrations.Count)
        {
            throw new InvalidOperationException("Database migration versions must be unique.");
        }
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        await CreateMigrationTableAsync(connection, cancellationToken);
        long currentVersion = await GetCurrentVersionAsync(connection, cancellationToken);

        foreach (Migration migration in _migrations.Where(migration => migration.Version > currentVersion))
        {
            await migration.ApplyAsync(connection, cancellationToken);
        }
    }

    private static async Task CreateMigrationTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS SchemaMigration (
                Version INTEGER NOT NULL PRIMARY KEY,
                Description TEXT NOT NULL,
                AppliedAtUtc TEXT NOT NULL
            );
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    private static async Task<long> GetCurrentVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigration;";

        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));
    }
}
