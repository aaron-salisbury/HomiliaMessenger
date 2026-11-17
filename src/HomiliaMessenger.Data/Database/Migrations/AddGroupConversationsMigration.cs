using Dapper;
using Microsoft.Data.Sqlite;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Database.Migrations;

/// <summary>
/// Lets a conversation be a group: a conversation gets a kind, and a group lists its contacts as participants, each with
/// their own memory checkpoint; every contact's message records who wrote it; and a group keeps whether its contacts may
/// start conversations, and when they last had the chance.
/// </summary>
/// <remarks>
/// <para>
/// A direct conversation keeps its contact in <c>Conversation.ContactId</c>, still unique, so a contact has at most one,
/// and its checkpoint stays in <c>Conversation.MemoryThroughMessageId</c>. A group has no single contact, so the column
/// becomes optional, which SQLite can only do by copying the table. Every column and row is copied as it is, under the
/// same IDs, so messages and memories keep pointing at their conversations.
/// </para>
/// <para>
/// A group is deleted with any of its contacts, as a direct conversation is with its contact. A group's
/// <c>GroupKey</c>, its contacts' IDs in order, is unique, so the same contacts cannot end up with two groups even when
/// two are started at once. Existing contact replies are attributed to their conversation's contact.
/// </para>
/// </remarks>
internal sealed class AddGroupConversationsMigration : Migration
{
    internal override long Version => 8;
    internal override string Description => "Add group conversations, their participants, and message senders";

    internal override bool RebuildsTables => true;

    protected override Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE Conversation_New (
                Id TEXT NOT NULL PRIMARY KEY,
                Kind INTEGER NOT NULL DEFAULT 0 CHECK(Kind IN (0, 1)),
                ContactId TEXT UNIQUE REFERENCES Contact(Id) ON DELETE CASCADE,
                GroupKey TEXT UNIQUE,
                Title TEXT,
                StartedAtUtc TEXT NOT NULL,
                MemoryNotes TEXT,
                MemoryThroughMessageId TEXT,
                Initiation INTEGER NOT NULL DEFAULT 1 CHECK(Initiation IN (0, 1)),
                LastInitiationAtUtc TEXT,
                CHECK((Kind = 0) = (ContactId IS NOT NULL)),
                CHECK((Kind = 1) = (GroupKey IS NOT NULL))
            );
            INSERT INTO Conversation_New (Id, Kind, ContactId, StartedAtUtc, MemoryNotes, MemoryThroughMessageId)
            SELECT Id, 0, ContactId, StartedAtUtc, MemoryNotes, MemoryThroughMessageId FROM Conversation;
            DROP TABLE Conversation;
            ALTER TABLE Conversation_New RENAME TO Conversation;

            CREATE TABLE ConversationParticipant (
                ConversationId TEXT NOT NULL REFERENCES Conversation(Id) ON DELETE CASCADE,
                ContactId TEXT NOT NULL REFERENCES Contact(Id) ON DELETE CASCADE,
                Position INTEGER NOT NULL,
                JoinedAtUtc TEXT NOT NULL,
                MemoryThroughMessageId TEXT,
                PRIMARY KEY (ConversationId, ContactId)
            );
            CREATE INDEX IX_ConversationParticipant_Contact ON ConversationParticipant(ContactId);
            CREATE TRIGGER TR_ConversationParticipant_Removed AFTER DELETE ON ConversationParticipant
            BEGIN
                DELETE FROM Conversation WHERE Id = OLD.ConversationId AND Kind = 1;
            END;

            ALTER TABLE ChatMessage ADD COLUMN SenderContactId TEXT REFERENCES Contact(Id) ON DELETE SET NULL;
            UPDATE ChatMessage
            SET SenderContactId = (SELECT c.ContactId FROM Conversation c WHERE c.Id = ChatMessage.ConversationId)
            WHERE Sender = 1;
            """;

        return connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: cancellationToken));
    }
}
