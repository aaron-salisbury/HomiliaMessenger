using Dapper;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Data.Database;
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

    // The last message is found by rowid so equal timestamps still resolve to the most recently inserted message.
    private const string SELECT_CONVERSATION_SQL = """
        SELECT c.Id, c.ContactId, c.StartedAtUtc, m.Id AS MessageId, m.Sender, m.Text, m.SentAtUtc, m.AgentProvider, m.AgentModel
        FROM Conversation c
        LEFT JOIN ChatMessage m ON m.rowid = (
            SELECT rowid FROM ChatMessage WHERE ConversationId = c.Id ORDER BY SentAtUtc DESC, rowid DESC LIMIT 1)
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

        return [.. rows.Select(Map)];
    }

    public async Task<Conversation?> GetAsync(ConversationId id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        ConversationRow? row = await connection.QuerySingleOrDefaultAsync<ConversationRow>(new CommandDefinition(
            SELECT_CONVERSATION_SQL + " WHERE c.Id = @Id", new { Id = ToKey(id.Value) }, cancellationToken: cancellationToken));

        return row is null ? null : Map(row);
    }

    public async Task<Conversation?> FindByContactAsync(ContactId contactId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        return await FindByContactAsync(connection, contactId, cancellationToken);
    }

    public async Task<Conversation> GetOrAddAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        const string sql = """
            INSERT INTO Conversation (Id, ContactId, StartedAtUtc)
            VALUES (@Id, @ContactId, @StartedAtUtc)
            ON CONFLICT(ContactId) DO NOTHING;
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = ToKey(conversation.Id.Value),
            ContactId = ToKey(conversation.ContactId.Value),
            StartedAtUtc = ToText(conversation.StartedAtUtc)
        }, cancellationToken: cancellationToken));

        return await FindByContactAsync(connection, conversation.ContactId, cancellationToken)
            ?? throw new InvalidOperationException("The conversation could not be saved.");
    }

    public async Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, ConversationId, Sender, Text, SentAtUtc, AgentProvider, AgentModel
            FROM ChatMessage
            WHERE ConversationId = @ConversationId
            ORDER BY SentAtUtc, rowid
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        IEnumerable<MessageRow> rows = await connection.QueryAsync<MessageRow>(new CommandDefinition(
            sql, new { ConversationId = ToKey(conversationId.Value) }, cancellationToken: cancellationToken));

        return [.. rows.Select(row => Map(row.Id, row.ConversationId, row.Sender, row.Text, row.SentAtUtc, row.AgentProvider, row.AgentModel))];
    }

    public async Task<bool> AddMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Inserting only when the conversation exists lets a reply that finishes after its conversation was deleted
        // report that, rather than failing on the foreign key.
        const string sql = """
            INSERT INTO ChatMessage (Id, ConversationId, Sender, Text, SentAtUtc, AgentProvider, AgentModel)
            SELECT @Id, @ConversationId, @Sender, @Text, @SentAtUtc, @AgentProvider, @AgentModel
            WHERE EXISTS (SELECT 1 FROM Conversation WHERE Id = @ConversationId);
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        int count = await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = ToKey(message.Id.Value),
            ConversationId = ToKey(message.ConversationId.Value),
            Sender = (int)message.Sender,
            message.Text,
            SentAtUtc = ToText(message.SentAtUtc),
            AgentProvider = message.Agent?.Provider,
            AgentModel = message.Agent?.Model
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

    private static async Task<Conversation?> FindByContactAsync(SqliteConnection connection, ContactId contactId, CancellationToken cancellationToken)
    {
        ConversationRow? row = await connection.QuerySingleOrDefaultAsync<ConversationRow>(new CommandDefinition(
            SELECT_CONVERSATION_SQL + " WHERE c.ContactId = @ContactId", new { ContactId = ToKey(contactId.Value) }, cancellationToken: cancellationToken));

        return row is null ? null : Map(row);
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

    private static Conversation Map(ConversationRow row)
    {
        ConversationId id = new(Guid.Parse(row.Id));

        return new()
        {
            Id = id,
            ContactId = new ContactId(Guid.Parse(row.ContactId)),
            StartedAtUtc = FromText(row.StartedAtUtc),
            LastMessage = row.MessageId is null || row.Sender is null || row.Text is null || row.SentAtUtc is null
                ? null
                : Map(row.MessageId, row.Id, row.Sender.Value, row.Text, row.SentAtUtc, row.AgentProvider, row.AgentModel)
        };
    }

    private static ChatMessage Map(string id, string conversationId, long sender, string text, string sentAtUtc, string? agentProvider, string? agentModel)
    {
        return new()
        {
            Id = new MessageId(Guid.Parse(id)),
            ConversationId = new ConversationId(Guid.Parse(conversationId)),
            Sender = (MessageSender)sender,
            Text = text,
            SentAtUtc = FromText(sentAtUtc),
            Agent = agentProvider is null || agentModel is null ? null : new AgentAssignment(agentProvider, agentModel)
        };
    }

    private sealed class ConversationRow
    {
        public string Id { get; set; } = string.Empty;
        public string ContactId { get; set; } = string.Empty;
        public string StartedAtUtc { get; set; } = string.Empty;
        public string? MessageId { get; set; }
        public long? Sender { get; set; }
        public string? Text { get; set; }
        public string? SentAtUtc { get; set; }
        public string? AgentProvider { get; set; }
        public string? AgentModel { get; set; }
    }

    private sealed class MessageRow
    {
        public string Id { get; set; } = string.Empty;
        public string ConversationId { get; set; } = string.Empty;
        public long Sender { get; set; }
        public string Text { get; set; } = string.Empty;
        public string SentAtUtc { get; set; } = string.Empty;
        public string? AgentProvider { get; set; }
        public string? AgentModel { get; set; }
    }
}
