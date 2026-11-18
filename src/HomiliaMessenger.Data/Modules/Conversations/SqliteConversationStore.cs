using Dapper;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Data.Database;
using HomiliaMessenger.Data.Modules.WebSearch;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Modules.Conversations;

internal sealed class SqliteConversationStore : IConversationStore
{
    private const string TIMESTAMP_FORMAT = "O";

    // The last message is found by rowid so equal timestamps still resolve to the most recently inserted message. Its
    // picture, if any, is not read: listing needs only what it shows, so it comes back with an empty PNG.
    private const string SELECT_CONVERSATION_SQL = """
        SELECT c.Id, c.Kind, c.ContactId, c.Title, c.StartedAtUtc, c.MemoryThroughMessageId, c.Initiation, c.LastInitiationAtUtc,
               m.Id AS MessageId, m.Sender, m.SenderContactId, m.Text, m.SentAtUtc, m.AgentProvider, m.AgentModel, m.WebSearch,
               CASE WHEN m.ImagePng IS NULL THEN NULL ELSE X'' END AS ImagePng, m.ImageDescription
        FROM Conversation c
        LEFT JOIN ChatMessage m ON m.rowid = (
            SELECT rowid FROM ChatMessage WHERE ConversationId = c.Id ORDER BY SentAtUtc DESC, rowid DESC LIMIT 1)
        """;

    private const string SELECT_PARTICIPANTS_SQL = """
        SELECT ConversationId, ContactId, MemoryThroughMessageId
        FROM ConversationParticipant
        """;

    private readonly IApplicationDatabase _database;

    public SqliteConversationStore(IApplicationDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        IEnumerable<ConversationRow> rows = await connection.QueryAsync<ConversationRow>(new CommandDefinition(
            SELECT_CONVERSATION_SQL + " ORDER BY COALESCE(m.SentAtUtc, c.StartedAtUtc) DESC, c.Id",
            cancellationToken: cancellationToken));
        IEnumerable<ParticipantRow> participants = await connection.QueryAsync<ParticipantRow>(new CommandDefinition(
            SELECT_PARTICIPANTS_SQL + " ORDER BY ConversationId, Position", cancellationToken: cancellationToken));
        ILookup<string, ParticipantRow> byConversation = participants.ToLookup(row => row.ConversationId);

        return [.. rows.Select(row => Map(row, byConversation[row.Id]))];
    }

    public async Task<Conversation?> GetAsync(ConversationId id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        return await GetAsync(connection, "c.Id = @Key", ToKey(id.Value), cancellationToken);
    }

    public async Task<Conversation?> FindByContactAsync(ContactId contactId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        return await GetAsync(connection, "c.ContactId = @Key", ToKey(contactId.Value), cancellationToken);
    }

    public async Task<Conversation> GetOrAddAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (conversation.ContactId is not { } contactId || conversation.IsGroup)
        {
            throw new ArgumentException("Only a direct conversation belongs to a contact.", nameof(conversation));
        }

        const string sql = """
            INSERT INTO Conversation (Id, Kind, ContactId, StartedAtUtc)
            VALUES (@Id, 0, @ContactId, @StartedAtUtc)
            ON CONFLICT(ContactId) DO NOTHING;
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = ToKey(conversation.Id.Value),
            ContactId = ToKey(contactId.Value),
            StartedAtUtc = ToText(conversation.StartedAtUtc)
        }, cancellationToken: cancellationToken));

        return await GetAsync(connection, "c.ContactId = @Key", ToKey(contactId.Value), cancellationToken)
            ?? throw new InvalidOperationException("The conversation could not be saved.");
    }

    public async Task<Conversation?> AddGroupAsync(Conversation group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (!group.IsGroup || group.ContactId is not null)
        {
            throw new ArgumentException("A group has participants rather than a single contact.", nameof(group));
        }

        // The same contacts in any order make the same key, which is unique, so a group started for contacts who already
        // have one (even at the same moment) is not saved.
        const string conversationSql = """
            INSERT INTO Conversation (Id, Kind, ContactId, GroupKey, Title, StartedAtUtc, Initiation)
            VALUES (@Id, 1, NULL, @GroupKey, @Title, @StartedAtUtc, @Initiation)
            ON CONFLICT(GroupKey) DO NOTHING;
            """;

        // Inserting only contacts that exist lets a contact deleted meanwhile be reported, rather than fail on the key.
        const string participantSql = """
            INSERT INTO ConversationParticipant (ConversationId, ContactId, Position, JoinedAtUtc)
            SELECT @ConversationId, Id, @Position, @JoinedAtUtc FROM Contact WHERE Id = @ContactId;
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction();

        string groupKey = GroupKey(group);
        int inserted = await connection.ExecuteAsync(new CommandDefinition(conversationSql, new
        {
            Id = ToKey(group.Id.Value),
            GroupKey = groupKey,
            group.Title,
            StartedAtUtc = ToText(group.StartedAtUtc),
            Initiation = (int)group.Initiation
        }, transaction, cancellationToken: cancellationToken));

        if (inserted == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await GetAsync(connection, "c.GroupKey = @Key", groupKey, cancellationToken);
        }

        for (int position = 0; position < group.Participants.Count; position++)
        {
            int added = await connection.ExecuteAsync(new CommandDefinition(participantSql, new
            {
                ConversationId = ToKey(group.Id.Value),
                ContactId = ToKey(group.Participants[position].ContactId.Value),
                Position = position,
                JoinedAtUtc = ToText(group.StartedAtUtc)
            }, transaction, cancellationToken: cancellationToken));

            if (added == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(connection, "c.Id = @Key", ToKey(group.Id.Value), cancellationToken);
    }

    /// <summary>
    /// A group's contacts' IDs, in order, identifying the group whatever order they joined in.
    /// </summary>
    private static string GroupKey(Conversation group)
    {
        return string.Join(",", group.ContactIds.Select(id => ToKey(id.Value)).Distinct().Order(StringComparer.Ordinal));
    }

    public async Task<bool> SetInitiationAsync(ConversationId id, GroupInitiation initiation, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        int count = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Conversation SET Initiation = @Initiation WHERE Id = @Id AND Kind = 1",
            new { Id = ToKey(id.Value), Initiation = (int)initiation }, cancellationToken: cancellationToken));

        return count != 0;
    }

    public async Task<bool> ClaimInitiationAsync(ConversationId id, DateTimeOffset at, DateTimeOffset? expected, CancellationToken cancellationToken = default)
    {
        // IS compares NULLs as equal, so a group that never had the chance matches.
        const string sql = """
            UPDATE Conversation SET LastInitiationAtUtc = @At
            WHERE Id = @Id AND Kind = 1 AND LastInitiationAtUtc IS @Expected;
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        int count = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = ToKey(id.Value),
            At = ToText(at),
            Expected = expected is null ? null : ToText(expected.Value)
        }, cancellationToken: cancellationToken));

        return count != 0;
    }

    public async Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(ConversationId conversationId, CancellationToken cancellationToken = default, bool withPictures = true)
    {
        // Without pictures, a message with one still says what it shows, with an empty PNG, as the conversation list does.
        string picture = withPictures ? "ImagePng" : "CASE WHEN ImagePng IS NULL THEN NULL ELSE X'' END AS ImagePng";
        string sql = $"""
            SELECT Id, ConversationId, Sender, SenderContactId, Text, SentAtUtc, AgentProvider, AgentModel, WebSearch, {picture}, ImageDescription
            FROM ChatMessage
            WHERE ConversationId = @ConversationId
            ORDER BY SentAtUtc, rowid
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        IEnumerable<MessageRow> rows = await connection.QueryAsync<MessageRow>(new CommandDefinition(
            sql, new { ConversationId = ToKey(conversationId.Value) }, cancellationToken: cancellationToken));

        return [.. rows.Select(row => Map(row.Id, row.ConversationId, row.Sender, row.SenderContactId, row.Text, row.SentAtUtc, row.AgentProvider, row.AgentModel, row.WebSearch, row.ImagePng, row.ImageDescription))];
    }

    public async Task<bool> AddMessageAsync(ChatMessage message, CancellationToken cancellationToken = default, bool onlyIfFirst = false)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Inserting only when the conversation exists lets a reply that finishes after its conversation was deleted
        // report that, rather than failing on the foreign key. A contact's message must come from someone taking part.
        const string sql = """
            INSERT INTO ChatMessage (Id, ConversationId, Sender, SenderContactId, Text, SentAtUtc, AgentProvider, AgentModel, WebSearch, ImagePng, ImageDescription)
            SELECT @Id, @ConversationId, @Sender, @SenderContactId, @Text, @SentAtUtc, @AgentProvider, @AgentModel, @WebSearch, @ImagePng, @ImageDescription
            WHERE EXISTS (
                SELECT 1 FROM Conversation c
                WHERE c.Id = @ConversationId
                  AND (@SenderContactId IS NULL
                       OR c.ContactId = @SenderContactId
                       OR EXISTS (SELECT 1 FROM ConversationParticipant p WHERE p.ConversationId = c.Id AND p.ContactId = @SenderContactId)))
              AND (@OnlyIfFirst = 0 OR NOT EXISTS (SELECT 1 FROM ChatMessage WHERE ConversationId = @ConversationId));
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        int count = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = ToKey(message.Id.Value),
            ConversationId = ToKey(message.ConversationId.Value),
            Sender = (int)message.Sender,
            SenderContactId = message.SenderContactId is null ? null : ToKey(message.SenderContactId.Value.Value),
            message.Text,
            SentAtUtc = ToText(message.SentAtUtc),
            AgentProvider = message.Agent?.Provider,
            AgentModel = message.Agent?.Model,
            WebSearch = message.WebSearch is null ? null : WebSearchJson.Write(message.WebSearch),
            ImagePng = message.Image?.Png,
            ImageDescription = message.Image?.Description,
            OnlyIfFirst = onlyIfFirst ? 1 : 0
        }, cancellationToken: cancellationToken));

        return count != 0;
    }

    public async Task<bool> DeleteAsync(ConversationId id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        int count = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Conversation WHERE Id = @Id", new { Id = ToKey(id.Value) }, cancellationToken: cancellationToken));

        return count != 0;
    }

    private static async Task<Conversation?> GetAsync(SqliteConnection connection, string condition, string key, CancellationToken cancellationToken)
    {
        ConversationRow? row = await connection.QuerySingleOrDefaultAsync<ConversationRow>(new CommandDefinition(
            SELECT_CONVERSATION_SQL + " WHERE " + condition, new { Key = key }, cancellationToken: cancellationToken));

        if (row is null)
        {
            return null;
        }

        IEnumerable<ParticipantRow> participants = await connection.QueryAsync<ParticipantRow>(new CommandDefinition(
            SELECT_PARTICIPANTS_SQL + " WHERE ConversationId = @Id ORDER BY Position", new { row.Id }, cancellationToken: cancellationToken));

        return Map(row, participants);
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

    private static MessageId? ToMessageId(string? key)
    {
        return key is null ? null : new MessageId(Guid.Parse(key));
    }

    private static Conversation Map(ConversationRow row, IEnumerable<ParticipantRow> participants)
    {
        ConversationKind kind = (ConversationKind)row.Kind;
        ContactId? contactId = row.ContactId is null ? null : new ContactId(Guid.Parse(row.ContactId));

        // A direct conversation's contact is its one participant, with the conversation's own checkpoint.
        IReadOnlyList<ConversationParticipant> members = kind == ConversationKind.Direct && contactId is { } direct
            ? [new ConversationParticipant(direct, ToMessageId(row.MemoryThroughMessageId))]
            : [.. participants.Select(participant => new ConversationParticipant(new ContactId(Guid.Parse(participant.ContactId)), ToMessageId(participant.MemoryThroughMessageId)))];

        return new()
        {
            Id = new ConversationId(Guid.Parse(row.Id)),
            Kind = kind,
            ContactId = contactId,
            Participants = members,
            Title = row.Title,
            StartedAtUtc = FromText(row.StartedAtUtc),
            Initiation = (GroupInitiation)row.Initiation,
            LastInitiationAtUtc = row.LastInitiationAtUtc is null ? null : FromText(row.LastInitiationAtUtc),
            LastMessage = row.MessageId is null || row.Sender is null || row.Text is null || row.SentAtUtc is null
                ? null
                : Map(row.MessageId, row.Id, row.Sender.Value, row.SenderContactId, row.Text, row.SentAtUtc, row.AgentProvider, row.AgentModel, row.WebSearch, row.ImagePng, row.ImageDescription)
        };
    }

    private static ChatMessage Map(string id, string conversationId, long sender, string? senderContactId, string text, string sentAtUtc, string? agentProvider, string? agentModel, string? webSearch, byte[]? imagePng, string? imageDescription)
    {
        return new()
        {
            Id = new MessageId(Guid.Parse(id)),
            ConversationId = new ConversationId(Guid.Parse(conversationId)),
            Sender = (MessageSender)sender,
            SenderContactId = senderContactId is null ? null : new ContactId(Guid.Parse(senderContactId)),
            Text = text,
            SentAtUtc = FromText(sentAtUtc),
            Agent = agentProvider is null || agentModel is null ? null : new AgentAssignment(agentProvider, agentModel),
            WebSearch = WebSearchJson.ReadLookup(webSearch),
            Image = imagePng is null ? null : new MessageImage(imagePng, imageDescription ?? "Sent a picture")
        };
    }

    private sealed class ConversationRow
    {
        public string Id { get; set; } = string.Empty;
        public long Kind { get; set; }
        public string? ContactId { get; set; }
        public string? Title { get; set; }
        public string StartedAtUtc { get; set; } = string.Empty;
        public string? MemoryThroughMessageId { get; set; }
        public long Initiation { get; set; }
        public string? LastInitiationAtUtc { get; set; }
        public string? MessageId { get; set; }
        public long? Sender { get; set; }
        public string? SenderContactId { get; set; }
        public string? Text { get; set; }
        public string? SentAtUtc { get; set; }
        public string? AgentProvider { get; set; }
        public string? AgentModel { get; set; }
        public string? WebSearch { get; set; }
        public byte[]? ImagePng { get; set; }
        public string? ImageDescription { get; set; }
    }

    private sealed class ParticipantRow
    {
        public string ConversationId { get; set; } = string.Empty;
        public string ContactId { get; set; } = string.Empty;
        public string? MemoryThroughMessageId { get; set; }
    }

    private sealed class MessageRow
    {
        public string Id { get; set; } = string.Empty;
        public string ConversationId { get; set; } = string.Empty;
        public long Sender { get; set; }
        public string? SenderContactId { get; set; }
        public string Text { get; set; } = string.Empty;
        public string SentAtUtc { get; set; } = string.Empty;
        public string? AgentProvider { get; set; }
        public string? AgentModel { get; set; }
        public string? WebSearch { get; set; }
        public byte[]? ImagePng { get; set; }
        public string? ImageDescription { get; set; }
    }
}
