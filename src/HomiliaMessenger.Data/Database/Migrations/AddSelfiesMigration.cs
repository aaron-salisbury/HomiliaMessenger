using Dapper;
using Microsoft.Data.Sqlite;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Database.Migrations;

/// <summary>
/// Lets a message carry a picture, such as a contact's selfie, with what it shows in words; and keeps the description a
/// contact's photo was generated from, so a selfie can draw the same person.
/// </summary>
internal sealed class AddSelfiesMigration : Migration
{
    internal override long Version => 10;
    internal override string Description => "Add message pictures and contact appearance";

    protected override Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            ALTER TABLE ChatMessage ADD COLUMN ImagePng BLOB;
            ALTER TABLE ChatMessage ADD COLUMN ImageDescription TEXT;
            ALTER TABLE Contact ADD COLUMN Appearance TEXT;
            """;

        return connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: cancellationToken));
    }
}
