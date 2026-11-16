using Dapper;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Database.Migrations;

/// <summary>
/// Stores what contacts remember as individual memories, with the vectors they are searched by, and imports each line of
/// the memory notes kept until now as a memory of its own.
/// </summary>
/// <remarks>
/// <para>
/// A memory references both its contact and the conversation it was learned in, and is removed with either. Its vector is
/// kept apart, at most one per memory, labelled with the embedding model and its length so vectors from different models
/// are never compared.
/// </para>
/// <para>
/// The import runs in the migration's transaction, so it either completes and is recorded, never to run again, or leaves
/// nothing behind and runs again at the next start. <c>Conversation.MemoryNotes</c> is left as it was, as a copy of the
/// notes as they stood, but is no longer read or written. <c>Conversation.MemoryThroughMessageId</c> keeps its meaning:
/// the last message condensed into memory.
/// </para>
/// </remarks>
internal sealed class AddContactMemoriesMigration : Migration
{
    internal const int IMPORTED_ORIGIN = 2;

    internal override long Version => 6;
    internal override string Description => "Add contact memories and their embeddings";

    protected override async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE Memory (
                Id TEXT NOT NULL PRIMARY KEY,
                ContactId TEXT NOT NULL REFERENCES Contact(Id) ON DELETE CASCADE,
                ConversationId TEXT NOT NULL REFERENCES Conversation(Id) ON DELETE CASCADE,
                Text TEXT NOT NULL CHECK(length(Text) > 0),
                Origin INTEGER NOT NULL CHECK(Origin IN (0, 1, 2)),
                SourceMessageId TEXT,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IX_Memory_Contact ON Memory(ContactId, CreatedAtUtc);
            CREATE INDEX IX_Memory_Conversation ON Memory(ConversationId);
            CREATE TABLE MemoryEmbedding (
                MemoryId TEXT NOT NULL PRIMARY KEY REFERENCES Memory(Id) ON DELETE CASCADE,
                Model TEXT NOT NULL,
                Dimensions INTEGER NOT NULL CHECK(Dimensions > 0),
                Vector BLOB NOT NULL,
                CreatedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IX_MemoryEmbedding_Model ON MemoryEmbedding(Model);
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: cancellationToken));
        await ImportNotesAsync(connection, transaction, cancellationToken);
    }

    private static async Task ImportNotesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        IEnumerable<NotesRow> rows = await connection.QueryAsync<NotesRow>(new CommandDefinition(
            "SELECT Id, ContactId, MemoryNotes FROM Conversation WHERE MemoryNotes IS NOT NULL",
            transaction: transaction,
            cancellationToken: cancellationToken));

        const string insertSql = """
            INSERT INTO Memory (Id, ContactId, ConversationId, Text, Origin, SourceMessageId, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@Id, @ContactId, @ConversationId, @Text, @Origin, NULL, @At, @At);
            """;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (NotesRow row in rows)
        {
            IReadOnlyList<string> lines = NoteLines(row.MemoryNotes);

            for (int index = 0; index < lines.Count; index++)
            {
                // A tick apart, so the imported memories keep the order the notes had.
                string at = now.AddTicks(index).ToString("O", CultureInfo.InvariantCulture);

                await connection.ExecuteAsync(new CommandDefinition(insertSql, new
                {
                    Id = Guid.NewGuid().ToString("D"),
                    row.ContactId,
                    ConversationId = row.Id,
                    Text = lines[index],
                    Origin = IMPORTED_ORIGIN,
                    At = at
                }, transaction, cancellationToken: cancellationToken));
            }
        }
    }

    // A memory's length limit when this migration was written; fixed here so the migration never changes.
    private const int MAX_LINE_LENGTH = 300;

    /// <summary>
    /// Each non-blank line of the notes, without a leading list marker, once each, in order. A line longer than a memory
    /// may be (the notes allowed a single line of 1,000 characters) is split, between sentences or words where it can
    /// be, so none of it is lost and every part fits within what an agent is given.
    /// </summary>
    internal static IReadOnlyList<string> NoteLines(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return [];
        }

        List<string> lines = [];

        foreach (string raw in notes.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.Trim();

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("• ", StringComparison.Ordinal))
            {
                line = line[2..].Trim();
            }

            foreach (string part in Split(line))
            {
                if (!lines.Contains(part, StringComparer.Ordinal))
                {
                    lines.Add(part);
                }
            }
        }

        return lines;
    }

    private static IEnumerable<string> Split(string line)
    {
        string rest = line;

        while (rest.Length > MAX_LINE_LENGTH)
        {
            int end = rest.LastIndexOfAny(['.', '!', '?', ';'], MAX_LINE_LENGTH - 1) + 1;

            if (end < MAX_LINE_LENGTH / 2)
            {
                end = rest.LastIndexOf(' ', MAX_LINE_LENGTH);
            }

            if (end < MAX_LINE_LENGTH / 2)
            {
                end = MAX_LINE_LENGTH;
            }

            string part = rest[..end].Trim();

            if (part.Length > 0)
            {
                yield return part;
            }

            rest = rest[end..].TrimStart();
        }

        if (rest.Length > 0)
        {
            yield return rest;
        }
    }

    private sealed class NotesRow
    {
        public string Id { get; set; } = string.Empty;
        public string ContactId { get; set; } = string.Empty;
        public string? MemoryNotes { get; set; }
    }
}
