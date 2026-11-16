using FluentValidation.Results;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.Credentials;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.WebSearch;

/// <summary>
/// Looks things up for contacts' replies: decides whether a message calls for a search, enforces the local credit
/// budget and the provider's limits, caches results, and never lets a failed search stop a reply.
/// </summary>
/// <remarks>
/// <para>
/// One search at most per reply, with no retries. Each search counts one credit against the period before it is sent,
/// atomically within the budget, so concurrent searches cannot overspend. The count is then corrected to what Tavily
/// reports the search cost; refunded when Tavily refused the request outright (bad key, rate limit, exhausted quota,
/// malformed request); and kept when the outcome is unknown, such as a timeout, since the search may have run.
/// </para>
/// <para>
/// Identical searches already in progress are shared rather than repeated, and run to completion even when the reply
/// that asked is canceled, so writing again (which restarts the reply) finds the results cached.
/// </para>
/// </remarks>
internal sealed class WebSearchService : IWebSearchService
{
    internal static readonly CredentialReference TAVILY_KEY = new("tavily-api-key");

    internal const int MAX_CONCURRENT_SEARCHES = 2;
    internal const int MAX_CACHE_ENTRIES = 300;
    internal const int CREDITS_PER_SEARCH = 1;
    internal static readonly TimeSpan SEARCH_TIMEOUT = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan TIME_SENSITIVE_CACHE_LIFETIME = TimeSpan.FromHours(3);
    internal static readonly TimeSpan CACHE_LIFETIME = TimeSpan.FromHours(24);
    internal static readonly TimeSpan DEFAULT_RATE_LIMIT_PAUSE = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MAX_RATE_LIMIT_PAUSE = TimeSpan.FromHours(1);

    private readonly IWebSearchStore _store;
    private readonly IWebSearchProvider _provider;
    private readonly ICredentialStore? _credentials;
    private readonly WebSearchSettingsValidator _validator = new();
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _concurrency = new(MAX_CONCURRENT_SEARCHES, MAX_CONCURRENT_SEARCHES);
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<WebSearchResult>?>>> _inFlight = new(StringComparer.Ordinal);

    // Provider state is changed only while holding this, from its current value, so concurrent searches never write back
    // a stale copy that undoes a pause or rejection another one recorded.
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    // Whether a key is saved, remembered so deciding about each message does not ask the credential store.
    private bool? _hasKey;

    // Changes whenever the key is saved or removed, so an outcome for an older key is never blamed on a newer one.
    private int _keyVersion;

    /// <param name="credentials">
    /// Where the API key is kept. Without a credential store (where the platform has none registered) no key can be
    /// saved, and contacts never search.
    /// </param>
    public WebSearchService(IWebSearchStore store, IWebSearchProvider provider, ICredentialStore? credentials = null)
        : this(store, provider, credentials, TimeProvider.System)
    {
    }

    internal WebSearchService(IWebSearchStore store, IWebSearchProvider provider, ICredentialStore? credentials, TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _credentials = credentials;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<WebSearchOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        WebSearchSettings settings = await _store.GetSettingsAsync(cancellationToken);
        WebSearchProviderState state = await _store.GetStateAsync(cancellationToken);
        bool hasKey = await HasKeyAsync(refresh: true, cancellationToken);
        DateOnly period = PeriodStart(Today(), settings.CreditResetDay);
        int used = await _store.GetCreditsUsedAsync(period, cancellationToken);

        return new WebSearchOverview(settings, hasKey, period, used, Math.Max(settings.MonthlyCreditBudget - used, 0), state);
    }

    public async Task<ValidationResult> SaveSettingsAsync(WebSearchSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ValidationResult result = await _validator.ValidateAsync(settings, cancellationToken);

        if (result.IsValid)
        {
            await _store.SaveSettingsAsync(settings, cancellationToken);
        }

        return result;
    }

    public async Task SetTavilyKeyAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        if (_credentials is null)
        {
            throw new InvalidOperationException("This system has no credential store to keep the key in.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await _credentials.DeleteAsync(TAVILY_KEY, cancellationToken);
            _hasKey = false;
        }
        else
        {
            await _credentials.StoreAsync(TAVILY_KEY, new CredentialMaterial(apiKey.Trim()), cancellationToken);
            _hasKey = true;
        }

        Interlocked.Increment(ref _keyVersion);

        // A rejected key no longer applies. A pause for an exhausted quota may still (the key may be on the same account)
        // until checking the key shows otherwise; the credits counted this period stay counted.
        await UpdateStateAsync(state => state with { KeyRejected = false, Problem = null }, cancellationToken);
    }

    public async Task<WebSearchKeyCheck> CheckTavilyKeyAsync(CancellationToken cancellationToken = default)
    {
        int keyVersion = Volatile.Read(ref _keyVersion);
        CredentialMaterial? key = await GetKeyAsync(cancellationToken);

        if (key is null)
        {
            return new WebSearchKeyCheck(WebSearchStatus.Unauthorized, Problem: "No Tavily API key is saved.");
        }

        WebSearchKeyCheck check = await _provider.CheckKeyAsync(key, cancellationToken);
        bool sameKey = keyVersion == Volatile.Read(ref _keyVersion);

        // What Tavily says about a key that has since been replaced says nothing about the saved one.
        if (check.Status == WebSearchStatus.Success && sameKey)
        {
            WebSearchSettings settings = await _store.GetSettingsAsync(cancellationToken);

            if (check.KeyCreditsUsed is { } reported)
            {
                await _store.RaiseCreditsAsync(PeriodStart(Today(), settings.CreditResetDay), reported, cancellationToken);
            }

            // Only usage below a reported limit shows credits left; a check that does not say keeps any quota pause.
            bool quotaLeft = check.PlanCreditLimit is { } limit && check.PlanCreditsUsed is { } planUsed && planUsed < limit;

            // Credits left lift a pause for an exhausted quota, never a rate limit's, which runs its course.
            await UpdateStateAsync(state =>
            {
                bool lift = quotaLeft && state.PausedForQuota;
                return new WebSearchProviderState(false, lift ? null : state.PausedUntilUtc, null, !lift && state.PausedForQuota);
            }, cancellationToken);
        }
        else if (check.Status == WebSearchStatus.Unauthorized && sameKey)
        {
            await UpdateStateAsync(state => state with { KeyRejected = true, Problem = check.Problem }, cancellationToken);
        }

        return check;
    }

    /// <summary>
    /// What to look up before the contact replies to the latest message in <paramref name="recent"/>, and what was found;
    /// <see langword="null"/> when web search is off, the message does not call for it, or the search failed or found
    /// nothing. Never throws except when canceled.
    /// </summary>
    /// <param name="privateNames">Names of the user and the contact, never sent in a query.</param>
    /// <param name="askModel">Asks the contact's model the yes-or-no question for an uncertain message.</param>
    /// <param name="onSearching">Called when a search is about to be sent.</param>
    internal async Task<WebLookup?> LookUpForReplyAsync(IReadOnlyList<ChatMessage> recent, IReadOnlyCollection<string> privateNames, Func<AgentRequest, CancellationToken, Task<AgentReply>> askModel, string model, Action? onSearching, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recent);
        ArgumentNullException.ThrowIfNull(privateNames);
        ArgumentNullException.ThrowIfNull(askModel);

        try
        {
            WebSearchSettings settings = await _store.GetSettingsAsync(cancellationToken);

            if (!settings.Enabled || !settings.TavilyEnabled || !await HasKeyAsync(refresh: false, cancellationToken))
            {
                return null;
            }

            DateOnly today = Today();
            SearchPlan plan = SearchDecision.Assess(recent, privateNames, today);

            if (plan.Need == SearchNeed.None || plan.Query is null)
            {
                return null;
            }

            string cacheKey = CacheKey(plan.Query, plan.TimeSensitive);
            IReadOnlyList<WebSearchResult>? cached = await _store.GetCachedAsync(cacheKey, _timeProvider.GetUtcNow(), cancellationToken);

            if (plan.Need == SearchNeed.Uncertain)
            {
                // Asking the model costs a second pass of a local model, so only ask when a search could follow.
                if (cached is null && !await CanSendAsync(settings, cancellationToken))
                {
                    return null;
                }

                AgentReply answer = await askModel(SearchDecision.ModelRequest(recent[^1].Text, model, today), cancellationToken);

                if (answer.Status != ReplyStatus.Success || !SearchDecision.IsYes(answer.Text))
                {
                    return null;
                }
            }

            IReadOnlyList<WebSearchResult>? results = cached;

            if (results is null)
            {
                onSearching?.Invoke();
                results = await SearchSharedAsync(cacheKey, new WebSearchRequest(plan.Query, plan.TimeSensitive), cancellationToken);
            }

            return results is { Count: > 0 } ? new WebLookup(plan.Query, [.. results.Take(WebContext.MAX_RESULTS)]) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A contact who cannot look something up still replies, from what they know.
            return null;
        }
    }

    /// <summary>
    /// Whether settings, the provider's limits, and the local budget allow sending a search now.
    /// </summary>
    private async Task<bool> CanSendAsync(WebSearchSettings settings, CancellationToken cancellationToken)
    {
        WebSearchProviderState state = await _store.GetStateAsync(cancellationToken);

        if (state.KeyRejected || state.PausedUntilUtc > _timeProvider.GetUtcNow())
        {
            return false;
        }

        int used = await _store.GetCreditsUsedAsync(PeriodStart(Today(), settings.CreditResetDay), cancellationToken);

        return used + CREDITS_PER_SEARCH <= settings.MonthlyCreditBudget;
    }

    /// <summary>
    /// Joins an identical search in progress, or starts one. The search runs on its own time limit, not the caller's.
    /// </summary>
    private async Task<IReadOnlyList<WebSearchResult>?> SearchSharedAsync(string cacheKey, WebSearchRequest request, CancellationToken cancellationToken)
    {
        Lazy<Task<IReadOnlyList<WebSearchResult>?>> search = _inFlight.GetOrAdd(cacheKey, key => new Lazy<Task<IReadOnlyList<WebSearchResult>?>>(() => SearchAsync(key, request)));
        Task<IReadOnlyList<WebSearchResult>?> task = search.Value;
        _ = task.ContinueWith(_ => _inFlight.TryRemove(KeyValuePair.Create(cacheKey, search)), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        return await task.WaitAsync(cancellationToken);
    }

    internal async Task<IReadOnlyList<WebSearchResult>?> SearchAsync(string cacheKey, WebSearchRequest request)
    {
        using CancellationTokenSource timeout = new(SEARCH_TIMEOUT, _timeProvider);
        CancellationToken cancellationToken = timeout.Token;

        await _concurrency.WaitAsync(cancellationToken);

        try
        {
            // Another search may have cached these results, or hit a limit, while this one waited.
            if (await _store.GetCachedAsync(cacheKey, _timeProvider.GetUtcNow(), cancellationToken) is { } cached)
            {
                return cached;
            }

            WebSearchSettings settings = await _store.GetSettingsAsync(cancellationToken);
            WebSearchProviderState state = await _store.GetStateAsync(cancellationToken);
            DateTimeOffset now = _timeProvider.GetUtcNow();

            if (!settings.Enabled || !settings.TavilyEnabled || state.KeyRejected || state.PausedUntilUtc > now)
            {
                return null;
            }

            int keyVersion = Volatile.Read(ref _keyVersion);
            CredentialMaterial? key = await GetKeyAsync(cancellationToken);

            if (key is null)
            {
                return null;
            }

            DateOnly period = PeriodStart(Today(), settings.CreditResetDay);

            if (!await _store.TryReserveCreditsAsync(period, CREDITS_PER_SEARCH, settings.MonthlyCreditBudget, cancellationToken))
            {
                return null;
            }

            WebSearchResponse response;

            try
            {
                response = await _provider.SearchAsync(request, key, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                response = WebSearchResponse.Failed(WebSearchStatus.Unavailable, "Tavily could not be reached.");
            }

            await RecordAsync(response, period, keyVersion, cancellationToken);

            if (response.Status != WebSearchStatus.Success)
            {
                return null;
            }

            IReadOnlyList<WebSearchResult> results = [.. response.Results.Where(IsUsable)];
            TimeSpan lifetime = request.TimeSensitive ? TIME_SENSITIVE_CACHE_LIFETIME : CACHE_LIFETIME;

            // Finding nothing is cached too, so the same question does not spend another credit right away.
            await _store.PutCachedAsync(cacheKey, results, now, now + lifetime, MAX_CACHE_ENTRIES, cancellationToken);

            return results;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>
    /// Corrects the credit count for the search's outcome, and remembers any limit the provider reported. A success never
    /// clears a pause or rejection, which other searches may have recorded meanwhile; those end when they expire, or when
    /// the key is changed or checked. Limits and problems are recorded only if the key the search used is still the saved
    /// one, so an answer about a replaced key never pauses or rejects its replacement; its credits are counted either way.
    /// </summary>
    private async Task RecordAsync(WebSearchResponse response, DateOnly period, int keyVersion, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // Unavailable or unreadable: the search may have run and been charged, so its credit stays counted. A request
        // Tavily refused outright was not charged.
        int creditCorrection = response.Status switch
        {
            WebSearchStatus.Success when response.CreditsUsed is { } credits and >= 0 => credits - CREDITS_PER_SEARCH,
            WebSearchStatus.Unauthorized or WebSearchStatus.RateLimited or WebSearchStatus.QuotaExceeded or WebSearchStatus.Rejected => -CREDITS_PER_SEARCH,
            _ => 0
        };

        bool sameKey = keyVersion == Volatile.Read(ref _keyVersion);

        // A refund for a replaced key's search could lower a count since raised to what Tavily reported for the new
        // key, so it is kept; counting a credit too many is the safe mistake.
        if (creditCorrection > 0 || (creditCorrection < 0 && sameKey))
        {
            await _store.AdjustCreditsAsync(period, creditCorrection, cancellationToken);
        }

        if (!sameKey)
        {
            return;
        }

        Func<WebSearchProviderState, WebSearchProviderState> change = response.Status switch
        {
            WebSearchStatus.Success => state => state.KeyRejected || state.PausedUntilUtc > now ? state : new WebSearchProviderState(),
            WebSearchStatus.Unauthorized => state => state with { KeyRejected = true, Problem = response.Problem },
            WebSearchStatus.RateLimited => state => state with
            {
                PausedUntilUtc = Later(state.PausedUntilUtc, now + RateLimitPause(response.RetryAfter)),
                PausedForQuota = state.PausedForQuota && state.PausedUntilUtc > now + RateLimitPause(response.RetryAfter),
                Problem = response.Problem
            },
            WebSearchStatus.QuotaExceeded => state => state with
            {
                PausedUntilUtc = Later(state.PausedUntilUtc, new DateTimeOffset(period.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)),
                PausedForQuota = true,
                Problem = response.Problem
            },
            _ => state => state with { Problem = response.Problem }
        };

        await UpdateStateAsync(change, cancellationToken);
    }

    private static TimeSpan RateLimitPause(TimeSpan? retryAfter)
    {
        TimeSpan pause = retryAfter is { } retry && retry > TimeSpan.Zero ? retry : DEFAULT_RATE_LIMIT_PAUSE;

        return pause > MAX_RATE_LIMIT_PAUSE ? MAX_RATE_LIMIT_PAUSE : pause;
    }

    /// <summary>
    /// Changes the provider state from its current stored value, one change at a time.
    /// </summary>
    private async Task UpdateStateAsync(Func<WebSearchProviderState, WebSearchProviderState> change, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);

        try
        {
            WebSearchProviderState current = await _store.GetStateAsync(cancellationToken);
            WebSearchProviderState updated = change(current);

            if (updated != current)
            {
                await _store.SaveStateAsync(updated, cancellationToken);
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private static DateTimeOffset Later(DateTimeOffset? current, DateTimeOffset candidate)
    {
        return current is { } existing && existing > candidate ? existing : candidate;
    }

    private async Task<bool> HasKeyAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (refresh || _hasKey is null)
        {
            await GetKeyAsync(cancellationToken);
        }

        return _hasKey == true;
    }

    private async Task<CredentialMaterial?> GetKeyAsync(CancellationToken cancellationToken)
    {
        CredentialMaterial? key = _credentials is null ? null : await _credentials.GetAsync(TAVILY_KEY, cancellationToken);
        _hasKey = key is not null;

        return key;
    }

    private DateOnly Today()
    {
        return DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// The start of the billing period containing <paramref name="today"/>: the most recent <paramref name="resetDay"/>.
    /// </summary>
    internal static DateOnly PeriodStart(DateOnly today, int resetDay)
    {
        int day = Math.Clamp(resetDay, 1, 28);
        DateOnly thisMonth = new(today.Year, today.Month, day);

        return today >= thisMonth ? thisMonth : thisMonth.AddMonths(-1);
    }

    /// <summary>
    /// The cache key for a query: the provider, the search kind, and the query in lowercase with single spaces. It holds
    /// only the query, so cached results are shared across contacts without carrying anything from a conversation.
    /// </summary>
    internal static string CacheKey(string query, bool timeSensitive)
    {
        string normalized = string.Join(' ', query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return $"tavily|basic|{(timeSensitive ? "recent" : "general")}|{normalized}";
    }

    private static bool IsUsable(WebSearchResult result)
    {
        return WebContext.Site(result.Url) is not null && (!string.IsNullOrWhiteSpace(result.Snippet) || !string.IsNullOrWhiteSpace(result.Title));
    }
}
