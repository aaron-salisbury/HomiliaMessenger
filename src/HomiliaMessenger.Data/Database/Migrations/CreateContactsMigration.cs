using Dapper;
using Microsoft.Data.Sqlite;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Database.Migrations;

internal sealed class CreateContactsMigration : Migration
{
    internal override long Version => 1;
    internal override string Description => "Create contact storage";

    protected override Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE Contact (
                Id TEXT NOT NULL PRIMARY KEY,
                GivenName TEXT,
                FamilyName TEXT,
                Nickname TEXT,
                Birthday TEXT,
                Gender TEXT,
                Personality TEXT,
                Background TEXT,
                AgentProvider TEXT,
                AgentModel TEXT,
                Photo BLOB,
                IsFavorite INTEGER NOT NULL CHECK(IsFavorite IN (0, 1)),
                CHECK((AgentProvider IS NULL) = (AgentModel IS NULL))
            );
            CREATE INDEX IX_Contact_Name ON Contact(FamilyName, GivenName);
            """;

        return connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: cancellationToken));
    }
}
