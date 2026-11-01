using Dapper;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Profile;
using HomiliaMessenger.Data.Database;
using Microsoft.Data.Sqlite;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Modules.Profile;

internal sealed class SqliteUserProfileStore : IUserProfileStore
{
    private readonly IApplicationDatabase _database;

    public SqliteUserProfileStore(IApplicationDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<UserProfile?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        UserProfileRow? row = await connection.QuerySingleOrDefaultAsync<UserProfileRow>(new CommandDefinition(
            "SELECT Name, Birthday, Gender, About, Photo FROM UserProfile WHERE Id = 1", cancellationToken: cancellationToken));

        return row is null ? null : new UserProfile
        {
            Name = row.Name,
            Birthday = ContactBirthday.TryParse(row.Birthday, out DateOnly birthday) ? birthday : null,
            Gender = row.Gender is null ? null : Enum.Parse<Gender>(row.Gender),
            About = row.About,
            Photo = row.Photo
        };
    }

    public async Task SaveAsync(UserProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        const string sql = """
            INSERT INTO UserProfile (Id, Name, Birthday, Gender, About, Photo)
            VALUES (1, @Name, @Birthday, @Gender, @About, @Photo)
            ON CONFLICT(Id) DO UPDATE SET
                Name=excluded.Name, Birthday=excluded.Birthday, Gender=excluded.Gender, About=excluded.About, Photo=excluded.Photo;
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            profile.Name,
            Birthday = profile.Birthday is null ? null : ContactBirthday.Format(profile.Birthday.Value),
            Gender = profile.Gender?.ToString(),
            profile.About,
            profile.Photo
        }, cancellationToken: cancellationToken));
    }

    private sealed class UserProfileRow
    {
        public string? Name { get; set; }
        public string? Birthday { get; set; }
        public string? Gender { get; set; }
        public string? About { get; set; }
        public byte[]? Photo { get; set; }
    }
}
