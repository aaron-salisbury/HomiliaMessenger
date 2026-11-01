using Dapper;
using Microsoft.Data.Sqlite;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Database.Migrations;

internal sealed class AddContactVoiceMigration : Migration
{
    internal override long Version => 5;
    internal override string Description => "Add optional contact speech voice";

    protected override Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(new CommandDefinition("ALTER TABLE Contact ADD COLUMN VoiceId TEXT;", transaction: transaction, cancellationToken: cancellationToken));
    }
}
