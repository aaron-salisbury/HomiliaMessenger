using Dapper;
using HomiliaMessenger.Business.Modules.WebSearch;
using HomiliaMessenger.Data.Database;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Modules.WebSearch;

/// <summary>
/// Web search's settings, provider state, credit usage, and result cache in the application database.
/// </summary>
internal sealed class SqliteWebSearchStore : IWebSearchStore
{
    private const string TIMESTAMP_FORMAT = "O";
    private const string DATE_FORMAT = "yyyy-MM-dd";
    private const string PROVIDER = "tavily";

    private readonly IApplicationDatabase _database;

    public SqliteWebSearchStore(IApplicationDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<WebSearchSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        SettingsRow? row = await connection.QuerySingleOrDefaultAsync<SettingsRow>(new CommandDefinition(
            "SELECT Enabled, TavilyEnabled, MonthlyCreditBudget, CreditResetDay FROM WebSearchSettings WHERE Id = 1",
            cancellationToken: cancellationToken));

        return row is null
            ? new WebSearchSettings()
            : new WebSearchSettings(row.Enabled != 0, row.TavilyEnabled != 0, (int)row.MonthlyCreditBudget, (int)row.CreditResetDay);
    }

    public async Task SaveSettingsAsync(WebSearchSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Leaves the provider state alone, which only searches change.
        const string sql = """
            INSERT INTO WebSearchSettings (Id, Enabled, TavilyEnabled, MonthlyCreditBudget, CreditResetDay)
            VALUES (1, @Enabled, @TavilyEnabled, @MonthlyCreditBudget, @CreditResetDay)
            ON CONFLICT(Id) DO UPDATE SET
                Enabled = excluded.Enabled,
                TavilyEnabled = excluded.TavilyEnabled,
                MonthlyCreditBudget = excluded.MonthlyCreditBudget,
                CreditResetDay = excluded.CreditResetDay;
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Enabled = settings.Enabled ? 1 : 0,
            TavilyEnabled = settings.TavilyEnabled ? 1 : 0,
            settings.MonthlyCreditBudget,
            settings.CreditResetDay
        }, cancellationToken: cancellationToken));
    }

    public async Task<WebSearchProviderState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        StateRow? row = await connection.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition(
            "SELECT KeyRejected, PausedUntilUtc, Problem, PausedForQuota FROM WebSearchSettings WHERE Id = 1",
            cancellationToken: cancellationToken));

        return row is null
            ? new WebSearchProviderState()
            : new WebSearchProviderState(row.KeyRejected != 0, row.PausedUntilUtc is null ? null : FromText(row.PausedUntilUtc), row.Problem, row.PausedForQuota != 0);
    }

    public async Task SaveStateAsync(WebSearchProviderState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        const string sql = """
            INSERT INTO WebSearchSettings (Id, Enabled, TavilyEnabled, MonthlyCreditBudget, CreditResetDay, KeyRejected, PausedUntilUtc, Problem, PausedForQuota)
            VALUES (1, 0, 1, @Budget, 1, @KeyRejected, @PausedUntilUtc, @Problem, @PausedForQuota)
            ON CONFLICT(Id) DO UPDATE SET
                KeyRejected = excluded.KeyRejected,
                PausedUntilUtc = excluded.PausedUntilUtc,
                PausedForQuota = excluded.PausedForQuota,
                Problem = excluded.Problem;
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Budget = WebSearchSettings.DEFAULT_MONTHLY_CREDIT_BUDGET,
            KeyRejected = state.KeyRejected ? 1 : 0,
            PausedUntilUtc = state.PausedUntilUtc is { } paused ? ToText(paused) : null,
            PausedForQuota = state.PausedForQuota ? 1 : 0,
            state.Problem
        }, cancellationToken: cancellationToken));
    }

    public async Task<int> GetCreditsUsedAsync(DateOnly periodStart, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COALESCE((SELECT Credits FROM WebSearchUsage WHERE Provider = @Provider AND PeriodStart = @Period), 0)",
            new { Provider = PROVIDER, Period = ToText(periodStart) },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> TryReserveCreditsAsync(DateOnly periodStart, int credits, int budget, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(credits);

        // A single conditional UPDATE: SQLite runs one write at a time, so the check and the increment cannot interleave
        // with another search's, on this connection or any other.
        const string sql = """
            INSERT INTO WebSearchUsage (Provider, PeriodStart, Credits) VALUES (@Provider, @Period, 0)
            ON CONFLICT(Provider, PeriodStart) DO NOTHING;
            UPDATE WebSearchUsage SET Credits = Credits + @Credits
            WHERE Provider = @Provider AND PeriodStart = @Period AND Credits + @Credits <= @Budget;
            SELECT changes();
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        long changed = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            sql, new { Provider = PROVIDER, Period = ToText(periodStart), Credits = credits, Budget = budget }, cancellationToken: cancellationToken));

        return changed == 1;
    }

    public async Task AdjustCreditsAsync(DateOnly periodStart, int delta, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO WebSearchUsage (Provider, PeriodStart, Credits) VALUES (@Provider, @Period, MAX(@Delta, 0))
            ON CONFLICT(Provider, PeriodStart) DO UPDATE SET Credits = MAX(Credits + @Delta, 0);
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            sql, new { Provider = PROVIDER, Period = ToText(periodStart), Delta = delta }, cancellationToken: cancellationToken));
    }

    public async Task RaiseCreditsAsync(DateOnly periodStart, int credits, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO WebSearchUsage (Provider, PeriodStart, Credits) VALUES (@Provider, @Period, MAX(@Credits, 0))
            ON CONFLICT(Provider, PeriodStart) DO UPDATE SET Credits = MAX(Credits, @Credits);
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            sql, new { Provider = PROVIDER, Period = ToText(periodStart), Credits = credits }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<WebSearchResult>?> GetCachedAsync(string key, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        string? json = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT Results FROM WebSearchCache WHERE CacheKey = @Key AND ExpiresAtUtc > @Now",
            new { Key = key, Now = ToText(now) },
            cancellationToken: cancellationToken));

        return json is null ? null : WebSearchJson.ReadResults(json);
    }

    public async Task PutCachedAsync(string key, IReadOnlyList<WebSearchResult> results, DateTimeOffset now, DateTimeOffset expiresAt, int maxEntries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(results);

        const string sql = """
            INSERT INTO WebSearchCache (CacheKey, Results, CreatedAtUtc, ExpiresAtUtc) VALUES (@Key, @Results, @Now, @ExpiresAt)
            ON CONFLICT(CacheKey) DO UPDATE SET Results = excluded.Results, CreatedAtUtc = excluded.CreatedAtUtc, ExpiresAtUtc = excluded.ExpiresAtUtc;
            DELETE FROM WebSearchCache WHERE ExpiresAtUtc <= @Now;
            DELETE FROM WebSearchCache WHERE CacheKey NOT IN (
                SELECT CacheKey FROM WebSearchCache ORDER BY CreatedAtUtc DESC LIMIT @MaxEntries);
            """;

        await using SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            Key = key,
            Results = WebSearchJson.Write(results),
            Now = ToText(now),
            ExpiresAt = ToText(expiresAt),
            MaxEntries = Math.Max(maxEntries, 1)
        }, cancellationToken: cancellationToken));
    }

    private static string ToText(DateTimeOffset timestamp)
    {
        return timestamp.ToUniversalTime().ToString(TIMESTAMP_FORMAT, CultureInfo.InvariantCulture);
    }

    private static string ToText(DateOnly date)
    {
        return date.ToString(DATE_FORMAT, CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset FromText(string text)
    {
        return DateTimeOffset.ParseExact(text, TIMESTAMP_FORMAT, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    private sealed class SettingsRow
    {
        public long Enabled { get; set; }
        public long TavilyEnabled { get; set; }
        public long MonthlyCreditBudget { get; set; }
        public long CreditResetDay { get; set; }
    }

    private sealed class StateRow
    {
        public long KeyRejected { get; set; }
        public string? PausedUntilUtc { get; set; }
        public long PausedForQuota { get; set; }
        public string? Problem { get; set; }
    }
}

/// <summary>
/// Search results and lookups as JSON, for the cache and for replies. Unreadable JSON reads as nothing rather than failing
/// the conversation it belongs to.
/// </summary>
internal static class WebSearchJson
{
    private static readonly JsonSerializerOptions OPTIONS = new(JsonSerializerDefaults.Web);

    internal static string Write(IReadOnlyList<WebSearchResult> results)
    {
        return JsonSerializer.Serialize(results, OPTIONS);
    }

    internal static string Write(WebLookup lookup)
    {
        return JsonSerializer.Serialize(lookup, OPTIONS);
    }

    internal static IReadOnlyList<WebSearchResult>? ReadResults(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<WebSearchResult>>(json, OPTIONS);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static WebLookup? ReadLookup(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WebLookup>(json, OPTIONS);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
