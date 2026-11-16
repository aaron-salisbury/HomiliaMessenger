using FluentValidation.Results;
using HomiliaMessenger.Business.Modules.Credentials;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.WebSearch;

/// <summary>
/// One web search result, the same whichever provider found it. Retrieved text is untrusted: it is shown to an agent
/// only as information, never as instructions.
/// </summary>
/// <param name="Url">An absolute <c>http</c> or <c>https</c> address.</param>
/// <param name="Snippet">A short excerpt of the page that matched the query.</param>
/// <param name="PublishedAt">When the page was published, when the provider knows.</param>
public sealed record WebSearchResult(string Title, string Url, string Snippet, DateOnly? PublishedAt = null);

/// <summary>
/// A search a provider is asked to run.
/// </summary>
/// <param name="Query">The query alone: never a conversation, memories, or profile details.</param>
/// <param name="TimeSensitive">Whether the question is about something recent, so older results matter less.</param>
public sealed record WebSearchRequest(string Query, bool TimeSensitive);

/// <summary>
/// Expected outcomes of a web search.
/// </summary>
public enum WebSearchStatus
{
    /// <summary>
    /// The search ran. It may still have found nothing.
    /// </summary>
    Success,

    /// <summary>
    /// The API key is missing, wrong, or revoked. Searching stops until the key is changed.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// Too many requests for now. Searching pauses for <see cref="WebSearchResponse.RetryAfter"/>, or a minute.
    /// </summary>
    RateLimited,

    /// <summary>
    /// The account's plan or spending limit is used up. Searching pauses until the next billing period.
    /// </summary>
    QuotaExceeded,

    /// <summary>
    /// The provider refused the request as malformed, so it did no work.
    /// </summary>
    Rejected,

    /// <summary>
    /// The provider could not be reached, did not answer in time, or failed on its side.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The provider answered with something that could not be read.
    /// </summary>
    InvalidResponse
}

/// <summary>
/// A provider's answer to a search.
/// </summary>
/// <param name="CreditsUsed">What the provider reported the search cost, when it said.</param>
/// <param name="Problem">A short explanation for any status but success, free of secrets and fit to show the user.</param>
public sealed record WebSearchResponse(WebSearchStatus Status, IReadOnlyList<WebSearchResult> Results, int? CreditsUsed = null, TimeSpan? RetryAfter = null, string? Problem = null)
{
    public static WebSearchResponse Failed(WebSearchStatus status, string problem, TimeSpan? retryAfter = null)
    {
        return new(status, [], null, retryAfter, problem);
    }
}

/// <summary>
/// What the provider says about an API key, without running a search.
/// </summary>
/// <param name="KeyCreditsUsed">Credits the key has used in the provider's current billing cycle.</param>
/// <param name="PlanCreditsUsed">Credits the whole account has used in that cycle.</param>
/// <param name="PlanCreditLimit">The account plan's credits per cycle.</param>
public sealed record WebSearchKeyCheck(WebSearchStatus Status, int? KeyCreditsUsed = null, int? PlanCreditsUsed = null, int? PlanCreditLimit = null, string? Problem = null);

/// <summary>
/// Capability to search the web through Tavily's hosted API. Implementations return expected failures as a status and
/// never log or return the key.
/// </summary>
public interface IWebSearchProvider
{
    /// <summary>
    /// Runs one basic search, which costs one credit.
    /// </summary>
    Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CredentialMaterial apiKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the provider whether <paramref name="apiKey"/> works and how much it has used, which costs no credits.
    /// </summary>
    Task<WebSearchKeyCheck> CheckKeyAsync(CredentialMaterial apiKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// How the user has set up web search. Searching sends queries to an outside service, so it is off until the user turns
/// it on.
/// </summary>
/// <param name="Enabled">Whether contacts may search the web at all.</param>
/// <param name="TavilyEnabled">Whether Tavily may be used, once a key is saved.</param>
/// <param name="MonthlyCreditBudget">
/// Tavily credits searches may use each billing period, as counted here. A safeguard, not the account's balance.
/// </param>
/// <param name="CreditResetDay">The day of the month the billing period starts on, from 1 to 28.</param>
public sealed record WebSearchSettings(bool Enabled = false, bool TavilyEnabled = true, int MonthlyCreditBudget = WebSearchSettings.DEFAULT_MONTHLY_CREDIT_BUDGET, int CreditResetDay = 1)
{
    /// <summary>
    /// Half of Tavily's free monthly allowance (1,000 credits as of November 2026), leaving room for other uses of the
    /// same account and for searches this count missed.
    /// </summary>
    public const int DEFAULT_MONTHLY_CREDIT_BUDGET = 500;
}

/// <summary>
/// Why Tavily is not being used for now, kept across restarts so a limit the provider reported is respected.
/// </summary>
/// <param name="KeyRejected">Tavily rejected the saved key; searching waits for a new one.</param>
/// <param name="PausedUntilUtc">No searches before this time, after a rate limit or an exhausted quota.</param>
/// <param name="Problem">The most recent problem, fit to show the user.</param>
/// <param name="PausedForQuota">
/// Whether the pause is for an exhausted quota, which checking the key can lift once Tavily shows credits left, rather
/// than for a rate limit, which always runs its course.
/// </param>
public sealed record WebSearchProviderState(bool KeyRejected = false, DateTimeOffset? PausedUntilUtc = null, string? Problem = null, bool PausedForQuota = false);

/// <summary>
/// Web search's settings and current state, for the Settings page.
/// </summary>
/// <param name="CreditsUsed">Credits counted here since <paramref name="PeriodStart"/>; an estimate.</param>
/// <param name="CreditsRemaining">Credits left in the local budget before searching pauses.</param>
public sealed record WebSearchOverview(WebSearchSettings Settings, bool HasTavilyKey, DateOnly PeriodStart, int CreditsUsed, int CreditsRemaining, WebSearchProviderState State)
{
    /// <summary>
    /// Whether a search could run right now.
    /// </summary>
    public bool IsReady => Settings.Enabled && Settings.TavilyEnabled && HasTavilyKey && !State.KeyRejected && CreditsRemaining > 0;
}

/// <summary>
/// The search behind a reply, kept with it so the user can see what was looked up and where the information came from.
/// </summary>
/// <param name="Query">Exactly what was sent to the provider.</param>
public sealed record WebLookup(string Query, IReadOnlyList<WebSearchResult> Sources);

/// <summary>
/// Settings and state of web search, for the user to manage.
/// </summary>
public interface IWebSearchService
{
    Task<WebSearchOverview> GetOverviewAsync(CancellationToken cancellationToken = default);

    Task<ValidationResult> SaveSettingsAsync(WebSearchSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the Tavily API key in the operating system's credential store, or forgets it when <paramref name="apiKey"/>
    /// is blank. A new key clears an earlier rejection; the credits counted this period are kept.
    /// </summary>
    Task SetTavilyKeyAsync(string? apiKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks Tavily whether the saved key works, without spending credits. When Tavily reports more credits used than were
    /// counted here, the count is raised to match.
    /// </summary>
    Task<WebSearchKeyCheck> CheckTavilyKeyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Persistence capability required by web search: settings, provider state, credit usage, and cached results.
/// </summary>
public interface IWebSearchStore
{
    Task<WebSearchSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(WebSearchSettings settings, CancellationToken cancellationToken = default);

    Task<WebSearchProviderState> GetStateAsync(CancellationToken cancellationToken = default);

    Task SaveStateAsync(WebSearchProviderState state, CancellationToken cancellationToken = default);

    Task<int> GetCreditsUsedAsync(DateOnly periodStart, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts <paramref name="credits"/> against the period, only if the total stays within <paramref name="budget"/>,
    /// as one atomic step so concurrent searches cannot overspend. Returns whether they were counted.
    /// </summary>
    Task<bool> TryReserveCreditsAsync(DateOnly periodStart, int credits, int budget, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds <paramref name="delta"/> (which may be negative) to the period's count, never going below zero.
    /// </summary>
    Task AdjustCreditsAsync(DateOnly periodStart, int delta, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raises the period's count to <paramref name="credits"/> if it is lower.
    /// </summary>
    Task RaiseCreditsAsync(DateOnly periodStart, int credits, CancellationToken cancellationToken = default);

    /// <summary>
    /// The cached results for <paramref name="key"/> unless they expired by <paramref name="now"/>.
    /// </summary>
    Task<IReadOnlyList<WebSearchResult>?> GetCachedAsync(string key, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Caches results until <paramref name="expiresAt"/>, removing expired entries and the oldest beyond
    /// <paramref name="maxEntries"/>.
    /// </summary>
    Task PutCachedAsync(string key, IReadOnlyList<WebSearchResult> results, DateTimeOffset now, DateTimeOffset expiresAt, int maxEntries, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stands in where no web search provider is registered: every search is unavailable, so contacts reply from what they
/// know.
/// </summary>
internal sealed class UnavailableWebSearchProvider : IWebSearchProvider
{
    private const string PROBLEM = "Web search is not available in this build.";

    public Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CredentialMaterial apiKey, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(WebSearchResponse.Failed(WebSearchStatus.Unavailable, PROBLEM));
    }

    public Task<WebSearchKeyCheck> CheckKeyAsync(CredentialMaterial apiKey, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new WebSearchKeyCheck(WebSearchStatus.Unavailable, Problem: PROBLEM));
    }
}
