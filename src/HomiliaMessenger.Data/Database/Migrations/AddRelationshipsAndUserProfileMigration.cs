using Dapper;
using Microsoft.Data.Sqlite;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Database.Migrations;

internal sealed class AddRelationshipsAndUserProfileMigration : Migration
{
    internal override long Version => 3;
    internal override string Description => "Add contact relationships and the user profile";

    protected override Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        // The profile table holds a single row.
        const string sql = """
            ALTER TABLE Contact ADD COLUMN Relationship TEXT;
            CREATE TABLE UserProfile (
                Id INTEGER NOT NULL PRIMARY KEY CHECK(Id = 1),
                Name TEXT,
                Birthday TEXT,
                Gender TEXT CHECK(Gender IN ('Male', 'Female')),
                About TEXT,
                Photo BLOB
            );
            """;

        return connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: cancellationToken));
    }
}
