using Dapper;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.Memories;
using HomiliaMessenger.Data.Database;
using Microsoft.Data.Sqlite;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Modules.Memories;

/// <summary>
/// Stores memories in SQLite, with each memory's vector as a BLOB of little-endian 32-bit floats. Similar memories are
/// found by comparing the query with every vector the contact has from the same model, exactly, in C#.
/// </summary>
/// <remarks>
/// A contact has at most a few thousand memories of a few hundred dimensions, which an exact comparison handles in
/// milliseconds without a native extension. Should that ever not be enough, an indexed search (such as the sqlite-vec
/// extension) can replace <see cref="FindSimilarAsync"/> without changing the contract.
/// </remarks>
internal sealed class SqliteMemoryStore : IMemoryStore
{
    private const string TIMESTAMP_FORMAT = "O";

    private const string SELECT_MEMORY_SQL = """
        SELECT m.Id, m.ContactId, m.ConversationId, m.Text, m.Origin, m.SourceMessageId, m.CreatedAtUtc, m.UpdatedAtUtc,
               e.Model AS EmbeddingModel, e.Dimensions AS EmbeddingDimensions, e.Vector
        FROM Memory m
        LEFT JOIN MemoryEmbedding e ON e.MemoryId = m.Id
        """;

    private readonly IApplicationDatabase _database;

    public SqliteMemoryStore(IApplicationDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<IReadOnlyList<Memory>> ListAsync(ContactId contactId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        IEnumerable<MemoryRow> rows = await connection.QueryAsync<MemoryRow>(new CommandDefinition(
            SELECT_MEMORY_SQL + " WHERE m.ContactId = @ContactId ORDER BY m.CreatedAtUtc, m.rowid",
            new { ContactId = ToKey(contactId.Value) },
            cancellationToken: cancellationToken));

        return [.. rows.Select(Map)];
    }

    public async Task<Memory?> GetAsync(MemoryId id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        MemoryRow? row = await connection.QuerySingleOrDefaultAsync<MemoryRow>(new CommandDefinition(
            SELECT_MEMORY_SQL + " WHERE m.Id = @Id", new { Id = ToKey(id.Value) }, cancellationToken: cancellationToken));

        return row is null ? null : Map(row);
    }

    public async Task<bool> AddAsync(Memory memory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memory);

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        return await InsertAsync(connection, null, memory, cancellationToken);
    }

    public async Task<bool> AddExtractedAsync(ConversationId conversationId, ContactId contactId, IReadOnlyList<Memory> memories, MessageId through, MessageId? expectedThrough, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memories);

        // A direct conversation keeps its contact's checkpoint itself; a group keeps one per participant. IS compares
        // NULLs as equal, so a conversation not yet condensed matches.
        const string checkpointSql = """
            UPDATE Conversation SET MemoryThroughMessageId = @Through
            WHERE Id = @Id AND Kind = 0 AND ContactId = @ContactId AND MemoryThroughMessageId IS @ExpectedThrough;
            UPDATE ConversationParticipant SET MemoryThroughMessageId = @Through
            WHERE ConversationId = @Id AND ContactId = @ContactId AND MemoryThroughMessageId IS @ExpectedThrough;
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction();

        int moved = await connection.ExecuteAsync(new CommandDefinition(checkpointSql, new
        {
            Id = ToKey(conversationId.Value),
            ContactId = ToKey(contactId.Value),
            Through = ToKey(through.Value),
            ExpectedThrough = expectedThrough is null ? null : ToKey(expectedThrough.Value.Value)
        }, transaction, cancellationToken: cancellationToken));

        if (moved == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        foreach (Memory memory in memories)
        {
            if (!await InsertAsync(connection, transaction, memory with { ConversationId = conversationId, ContactId = contactId }, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdateAsync(MemoryId id, string text, MemoryOrigin origin, DateTimeOffset updatedAtUtc, string expectedText, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(expectedText);

        const string updateSql = """
            UPDATE Memory SET Text = @Text, Origin = @Origin, UpdatedAtUtc = @UpdatedAtUtc
            WHERE Id = @Id AND Text = @ExpectedText;
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction();

        int count = await connection.ExecuteAsync(new CommandDefinition(updateSql, new
        {
            Id = ToKey(id.Value),
            Text = text,
            Origin = (int)origin,
            UpdatedAtUtc = ToText(updatedAtUtc),
            ExpectedText = expectedText
        }, transaction, cancellationToken: cancellationToken));

        if (count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        // The vector described the old text.
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM MemoryEmbedding WHERE MemoryId = @Id", new { Id = ToKey(id.Value) }, transaction, cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(MemoryId id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        int count = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Memory WHERE Id = @Id", new { Id = ToKey(id.Value) }, cancellationToken: cancellationToken));

        return count != 0;
    }

    public async Task<IReadOnlyList<Memory>> ListUnembeddedAsync(ContactId? contactId, string model, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        string sql = SELECT_MEMORY_SQL + """

            WHERE (e.Model IS NULL OR e.Model <> @Model OR e.Vector IS NULL OR length(e.Vector) <> e.Dimensions * 4)
              AND (@ContactId IS NULL OR m.ContactId = @ContactId)
            ORDER BY m.CreatedAtUtc, m.rowid
            LIMIT @Limit
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        // A stored vector that cannot be read is embedded again, as if it were missing.
        IEnumerable<MemoryRow> rows = await connection.QueryAsync<MemoryRow>(new CommandDefinition(sql, new
        {
            Model = model,
            ContactId = contactId is null ? null : ToKey(contactId.Value.Value),
            Limit = limit
        }, cancellationToken: cancellationToken));

        return [.. rows.Select(Map)];
    }

    public async Task<bool> SaveEmbeddingAsync(MemoryId id, string embeddedText, string model, ReadOnlyMemory<float> vector, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embeddedText);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        if (!VectorSimilarity.IsUsable(vector.Span))
        {
            throw new ArgumentException("The vector is empty or has values that are not finite numbers.", nameof(vector));
        }

        // Saved only while the memory still has the text that was embedded, so a correction made meanwhile is never
        // given a vector that describes the old text.
        const string sql = """
            INSERT INTO MemoryEmbedding (MemoryId, Model, Dimensions, Vector, CreatedAtUtc)
            SELECT @Id, @Model, @Dimensions, @Vector, @CreatedAtUtc
            WHERE EXISTS (SELECT 1 FROM Memory WHERE Id = @Id AND Text = @Text)
            ON CONFLICT(MemoryId) DO UPDATE SET
                Model = excluded.Model, Dimensions = excluded.Dimensions, Vector = excluded.Vector, CreatedAtUtc = excluded.CreatedAtUtc;
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        int count = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = ToKey(id.Value),
            Text = embeddedText,
            Model = model,
            Dimensions = vector.Length,
            Vector = ToBytes(vector.Span),
            CreatedAtUtc = ToText(DateTimeOffset.UtcNow)
        }, cancellationToken: cancellationToken));

        return count != 0;
    }

    public async Task<IReadOnlyList<ScoredMemory>> FindSimilarAsync(ContactId contactId, string model, ReadOnlyMemory<float> query, int count, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        if (count < 1 || !VectorSimilarity.IsUsable(query.Span))
        {
            return [];
        }

        string sql = """
            SELECT m.Id, m.ContactId, m.ConversationId, m.Text, m.Origin, m.SourceMessageId, m.CreatedAtUtc, m.UpdatedAtUtc,
                   e.Model AS EmbeddingModel, e.Dimensions AS EmbeddingDimensions, e.Vector
            FROM Memory m
            JOIN MemoryEmbedding e ON e.MemoryId = m.Id
            WHERE m.ContactId = @ContactId AND e.Model = @Model AND e.Dimensions = @Dimensions
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        IEnumerable<MemoryRow> rows = await connection.QueryAsync<MemoryRow>(new CommandDefinition(sql, new
        {
            ContactId = ToKey(contactId.Value),
            Model = model,
            Dimensions = query.Length
        }, cancellationToken: cancellationToken));

        List<ScoredMemory> scored = [];
        List<string> unreadable = [];

        foreach (MemoryRow row in rows)
        {
            // A vector of the wrong size or with values that are not numbers is skipped, never compared; the memory is
            // still recalled by its words.
            if (FromBytes(row.Vector, query.Length) is not { } vector)
            {
                unreadable.Add(row.Id);
                continue;
            }

            scored.Add(new ScoredMemory(Map(row), VectorSimilarity.Cosine(query.Span, vector), vector));
        }

        if (unreadable.Count > 0)
        {
            // Discarded, so the memory is embedded again on the next pass.
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM MemoryEmbedding WHERE MemoryId IN @Ids", new { Ids = unreadable }, cancellationToken: cancellationToken));
        }

        return [.. scored.OrderByDescending(memory => memory.Score).ThenByDescending(memory => memory.Memory.CreatedAtUtc).Take(count)];
    }

    private static async Task<bool> InsertAsync(SqliteConnection connection, SqliteTransaction? transaction, Memory memory, CancellationToken cancellationToken)
    {
        // The memory belongs to a direct conversation's contact, or to the group participant it names; inserting only
        // when the conversation (and participant) exists lets work that finishes after its conversation was deleted
        // report that, rather than fail on the foreign key.
        const string sql = """
            INSERT INTO Memory (Id, ContactId, ConversationId, Text, Origin, SourceMessageId, CreatedAtUtc, UpdatedAtUtc)
            SELECT @Id, COALESCE(c.ContactId, p.ContactId), c.Id, @Text, @Origin, @SourceMessageId, @CreatedAtUtc, @UpdatedAtUtc
            FROM Conversation c
            LEFT JOIN ConversationParticipant p ON p.ConversationId = c.Id AND p.ContactId = @ContactId
            WHERE c.Id = @ConversationId AND (c.ContactId IS NOT NULL OR p.ContactId IS NOT NULL);
            """;

        int count = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = ToKey(memory.Id.Value),
            ContactId = ToKey(memory.ContactId.Value),
            ConversationId = ToKey(memory.ConversationId.Value),
            memory.Text,
            Origin = (int)memory.Origin,
            SourceMessageId = memory.SourceMessageId is null ? null : ToKey(memory.SourceMessageId.Value.Value),
            CreatedAtUtc = ToText(memory.CreatedAtUtc),
            UpdatedAtUtc = ToText(memory.UpdatedAtUtc)
        }, transaction, cancellationToken: cancellationToken));

        return count != 0;
    }

    internal static byte[] ToBytes(ReadOnlySpan<float> vector)
    {
        byte[] bytes = new byte[vector.Length * sizeof(float)];

        for (int index = 0; index < vector.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float)), vector[index]);
        }

        return bytes;
    }

    /// <summary>
    /// The vector stored in <paramref name="bytes"/>, or <see langword="null"/> when it is not exactly
    /// <paramref name="dimensions"/> usable values.
    /// </summary>
    internal static float[]? FromBytes(byte[]? bytes, int dimensions)
    {
        if (bytes is null || dimensions < 1 || bytes.Length != dimensions * sizeof(float))
        {
            return null;
        }

        float[] vector = new float[dimensions];

        for (int index = 0; index < dimensions; index++)
        {
            vector[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(index * sizeof(float)));
        }

        return VectorSimilarity.IsUsable(vector) ? vector : null;
    }

    private static string ToKey(Guid id)
    {
        return id.ToString("D");
    }

    private static string ToText(DateTimeOffset timestamp)
    {
        return timestamp.ToUniversalTime().ToString(TIMESTAMP_FORMAT, CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset FromText(string text)
    {
        return DateTimeOffset.ParseExact(text, TIMESTAMP_FORMAT, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    /// <remarks>
    /// A memory whose stored vector cannot be read is reported as not embedded, so it is recalled by its words and
    /// embedded again rather than left out of both.
    /// </remarks>
    private static Memory Map(MemoryRow row)
    {
        bool embedded = row.EmbeddingDimensions is { } dimensions && dimensions <= int.MaxValue / sizeof(float)
            && FromBytes(row.Vector, (int)dimensions) is not null;

        return new()
        {
            Id = new MemoryId(Guid.Parse(row.Id)),
            ContactId = new ContactId(Guid.Parse(row.ContactId)),
            ConversationId = new ConversationId(Guid.Parse(row.ConversationId)),
            Text = row.Text,
            Origin = Enum.IsDefined((MemoryOrigin)row.Origin) ? (MemoryOrigin)row.Origin : MemoryOrigin.Manual,
            SourceMessageId = row.SourceMessageId is null ? null : new MessageId(Guid.Parse(row.SourceMessageId)),
            CreatedAtUtc = FromText(row.CreatedAtUtc),
            UpdatedAtUtc = FromText(row.UpdatedAtUtc),
            EmbeddingModel = embedded ? row.EmbeddingModel : null,
            EmbeddingDimensions = embedded ? (int)row.EmbeddingDimensions!.Value : null
        };
    }

    private sealed class MemoryRow
    {
        public string Id { get; set; } = string.Empty;
        public string ContactId { get; set; } = string.Empty;
        public string ConversationId { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public long Origin { get; set; }
        public string? SourceMessageId { get; set; }
        public string CreatedAtUtc { get; set; } = string.Empty;
        public string UpdatedAtUtc { get; set; } = string.Empty;
        public string? EmbeddingModel { get; set; }
        public long? EmbeddingDimensions { get; set; }
        public byte[]? Vector { get; set; }
    }
}
