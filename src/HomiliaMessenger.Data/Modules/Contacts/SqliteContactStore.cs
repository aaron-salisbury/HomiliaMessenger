using Dapper;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Data.Database;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Modules.Contacts;

internal sealed class SqliteContactStore : IContactStore
{
    private const string SELECT_CONTACT_SQL = """
        SELECT Id, GivenName, FamilyName, Nickname, Birthday, Gender, Personality, Background, AgentProvider, AgentModel, Photo, IsFavorite
        FROM Contact
        """;

    private readonly IApplicationDatabase _database;

    public SqliteContactStore(IApplicationDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<IReadOnlyList<Contact>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        IEnumerable<ContactRow> rows = await connection.QueryAsync<ContactRow>(new CommandDefinition(
            SELECT_CONTACT_SQL + " ORDER BY IsFavorite DESC, COALESCE(Nickname, GivenName, FamilyName) COLLATE NOCASE, FamilyName COLLATE NOCASE, Id",
            cancellationToken: cancellationToken));

        return [.. rows.Select(Map)];
    }

    public async Task<Contact?> GetAsync(ContactId id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        ContactRow? row = await connection.QuerySingleOrDefaultAsync<ContactRow>(new CommandDefinition(
            SELECT_CONTACT_SQL + " WHERE Id = @Id", new { Id = ToKey(id) }, cancellationToken: cancellationToken));

        return row is null ? null : Map(row);
    }

    public async Task UpsertAsync(Contact contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);

        const string sql = """
            INSERT INTO Contact (Id, GivenName, FamilyName, Nickname, Birthday, Gender, Personality, Background, AgentProvider, AgentModel, Photo, IsFavorite)
            VALUES (@Id, @GivenName, @FamilyName, @Nickname, @Birthday, @Gender, @Personality, @Background, @AgentProvider, @AgentModel, @Photo, @IsFavorite)
            ON CONFLICT(Id) DO UPDATE SET
                GivenName=excluded.GivenName, FamilyName=excluded.FamilyName, Nickname=excluded.Nickname,
                Birthday=excluded.Birthday, Gender=excluded.Gender, Personality=excluded.Personality, Background=excluded.Background,
                AgentProvider=excluded.AgentProvider, AgentModel=excluded.AgentModel,
                Photo=excluded.Photo, IsFavorite=excluded.IsFavorite;
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Id = ToKey(contact.Id),
            contact.GivenName,
            contact.FamilyName,
            contact.Nickname,
            Birthday = contact.Birthday is null ? null : ContactBirthday.Format(contact.Birthday.Value),
            contact.Gender,
            contact.Personality,
            contact.Background,
            AgentProvider = contact.Agent?.Provider,
            AgentModel = contact.Agent?.Model,
            contact.Photo,
            contact.IsFavorite
        }, cancellationToken: cancellationToken));
    }

    public async Task<bool> DeleteAsync(ContactId id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        int count = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Contact WHERE Id = @Id", new { Id = ToKey(id) }, cancellationToken: cancellationToken));

        return count != 0;
    }

    private static string ToKey(ContactId id)
    {
        return id.Value.ToString("D");
    }

    private static Contact Map(ContactRow row)
    {
        return new()
        {
            Id = new ContactId(Guid.Parse(row.Id)),
            GivenName = row.GivenName,
            FamilyName = row.FamilyName,
            Nickname = row.Nickname,
            Birthday = ContactBirthday.TryParse(row.Birthday, out DateOnly birthday) ? birthday : null,
            Gender = row.Gender,
            Personality = row.Personality,
            Background = row.Background,
            Agent = row.AgentProvider is null || row.AgentModel is null ? null : new AgentAssignment(row.AgentProvider, row.AgentModel),
            Photo = row.Photo,
            IsFavorite = row.IsFavorite
        };
    }

    private sealed class ContactRow
    {
        public string Id { get; set; } = string.Empty;
        public string? GivenName { get; set; }
        public string? FamilyName { get; set; }
        public string? Nickname { get; set; }
        public string? Birthday { get; set; }
        public string? Gender { get; set; }
        public string? Personality { get; set; }
        public string? Background { get; set; }
        public string? AgentProvider { get; set; }
        public string? AgentModel { get; set; }
        public byte[]? Photo { get; set; }
        public bool IsFavorite { get; set; }
    }
}
