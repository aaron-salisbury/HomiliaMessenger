using HomiliaMessenger.Business.Modules.Credentials;
using HomiliaMessenger.Business.Modules.WebSearch;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.WebSearch;

/// <summary>
/// Searches the web through Tavily's hosted API. Only basic searches are run (one credit each); answers, raw page
/// content, images, and automatic parameters, which can cost more, are never requested.
/// </summary>
/// <remarks>
/// Request and response formats, credit costs, and status codes follow Tavily's API reference as of November 2026:
/// <see href="https://docs.tavily.com/documentation/api-reference/endpoint/search"/> and
/// <see href="https://docs.tavily.com/documentation/api-reference/endpoint/usage"/>.
/// </remarks>
internal sealed class TavilyWebSearchProvider : IWebSearchProvider
{
    internal const string SEARCH_URL = "https://api.tavily.com/search";
    internal const string USAGE_URL = "https://api.tavily.com/usage";
    internal const int MAX_RESULTS = 5;
    internal const int MAX_RESPONSE_BYTES = 512 * 1024;
    internal static readonly TimeSpan REQUEST_TIMEOUT = TimeSpan.FromSeconds(15);

    // Tavily's own status codes for exhausted plan and pay-as-you-go limits.
    private const int PLAN_LIMIT_EXCEEDED = 432;
    private const int PAY_AS_YOU_GO_LIMIT_EXCEEDED = 433;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TavilyWebSearchProvider> _logger;

    public TavilyWebSearchProvider(IHttpClientFactory httpClientFactory, ILogger<TavilyWebSearchProvider> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CredentialMaterial apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(apiKey);

        Dictionary<string, object> body = new()
        {
            ["query"] = request.Query,
            ["search_depth"] = "basic",
            ["topic"] = "general",
            ["max_results"] = MAX_RESULTS,
            ["chunks_per_source"] = 1,
            ["include_answer"] = false,
            ["include_raw_content"] = false,
            ["include_images"] = false,
            ["include_favicon"] = false,
            ["include_published_date"] = true,
            ["auto_parameters"] = false,
            ["include_usage"] = true
        };

        if (request.TimeSensitive)
        {
            body["time_range"] = "year";
        }

        using HttpRequestMessage message = new(HttpMethod.Post, SEARCH_URL)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

        Outcome outcome = await SendAsync(message, apiKey, cancellationToken);

        if (outcome.Failure is { } failure)
        {
            return failure;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(outcome.Body!);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Tavily returned a search response that is not valid JSON.");
            return WebSearchResponse.Failed(WebSearchStatus.InvalidResponse, "Tavily sent a response that could not be read.");
        }
    }

    public async Task<WebSearchKeyCheck> CheckKeyAsync(CredentialMaterial apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        using HttpRequestMessage message = new(HttpMethod.Get, USAGE_URL);
        Outcome outcome = await SendAsync(message, apiKey, cancellationToken);

        if (outcome.Failure is { } failure)
        {
            return new WebSearchKeyCheck(failure.Status, Problem: failure.Problem);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(outcome.Body!);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return new WebSearchKeyCheck(WebSearchStatus.InvalidResponse, Problem: "Tavily sent a response that could not be read.");
            }

            int? keyUsed = null;
            int? planUsed = null;
            int? planLimit = null;

            if (root.TryGetProperty("key", out JsonElement key) && key.ValueKind == JsonValueKind.Object)
            {
                keyUsed = Number(key, "search_usage") ?? Number(key, "usage");
            }

            if (root.TryGetProperty("account", out JsonElement account) && account.ValueKind == JsonValueKind.Object)
            {
                planUsed = Number(account, "plan_usage");
                planLimit = Number(account, "plan_limit");
            }

            return new WebSearchKeyCheck(WebSearchStatus.Success, keyUsed, planUsed, planLimit);
        }
        catch (JsonException)
        {
            return new WebSearchKeyCheck(WebSearchStatus.InvalidResponse, Problem: "Tavily sent a response that could not be read.");
        }
    }

    /// <summary>
    /// The search results in a successful response, as app results. A result without an <c>http</c> or <c>https</c> URL
    /// is dropped; a response without a results list is invalid.
    /// </summary>
    internal static WebSearchResponse Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
        {
            return WebSearchResponse.Failed(WebSearchStatus.InvalidResponse, "Tavily sent a response without search results.");
        }

        List<WebSearchResult> parsed = [];

        foreach (JsonElement item in results.EnumerateArray())
        {
            if (parsed.Count == MAX_RESULTS)
            {
                break;
            }

            if (item.ValueKind != JsonValueKind.Object
                || Text(item, "url") is not { } url
                || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                continue;
            }

            parsed.Add(new WebSearchResult(Text(item, "title") ?? string.Empty, uri.AbsoluteUri, Text(item, "content") ?? string.Empty, Date(Text(item, "published_date"))));
        }

        int? credits = root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object ? Number(usage, "credits") : null;

        return new WebSearchResponse(WebSearchStatus.Success, parsed, credits);
    }

    /// <summary>
    /// Sends a request with the key, within <see cref="REQUEST_TIMEOUT"/>, and reads at most
    /// <see cref="MAX_RESPONSE_BYTES"/> of a successful response. Expected failures become a status; only the caller
    /// canceling throws.
    /// </summary>
    private async Task<Outcome> SendAsync(HttpRequestMessage message, CredentialMaterial apiKey, CancellationToken cancellationToken)
    {
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Value);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(REQUEST_TIMEOUT);

        try
        {
            HttpClient client = _httpClientFactory.CreateClient();
            using HttpResponseMessage response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Tavily answered {Path} with HTTP {Status}.", message.RequestUri?.AbsolutePath, status);
                return new Outcome(null, Failure(response, status));
            }

            if (response.Content.Headers.ContentLength > MAX_RESPONSE_BYTES)
            {
                return new Outcome(null, WebSearchResponse.Failed(WebSearchStatus.InvalidResponse, "Tavily sent a response that was too large."));
            }

            string? body = await ReadLimitedAsync(response.Content, timeout.Token).ConfigureAwait(false);

            return body is null
                ? new Outcome(null, WebSearchResponse.Failed(WebSearchStatus.InvalidResponse, "Tavily sent a response that was too large."))
                : new Outcome(body, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Tavily did not answer within {Seconds} seconds.", REQUEST_TIMEOUT.TotalSeconds);
            return new Outcome(null, WebSearchResponse.Failed(WebSearchStatus.Unavailable, "Tavily did not answer in time."));
        }
        catch (HttpRequestException error)
        {
            _logger.LogWarning("Tavily could not be reached: {Error}", error.HttpRequestError);
            return new Outcome(null, WebSearchResponse.Failed(WebSearchStatus.Unavailable, "Tavily could not be reached. Check the internet connection."));
        }
    }

    private static WebSearchResponse Failure(HttpResponseMessage response, int status)
    {
        return status switch
        {
            (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden =>
                WebSearchResponse.Failed(WebSearchStatus.Unauthorized, "Tavily rejected the API key. Check it in Settings."),
            (int)HttpStatusCode.TooManyRequests =>
                WebSearchResponse.Failed(WebSearchStatus.RateLimited, "Tavily is limiting requests; searching will resume shortly.", RetryAfter(response)),
            PLAN_LIMIT_EXCEEDED =>
                WebSearchResponse.Failed(WebSearchStatus.QuotaExceeded, "The Tavily plan's credits are used up for this billing period."),
            PAY_AS_YOU_GO_LIMIT_EXCEEDED =>
                WebSearchResponse.Failed(WebSearchStatus.QuotaExceeded, "The Tavily pay-as-you-go limit was reached. It can be raised in Tavily's dashboard."),
            (int)HttpStatusCode.BadRequest or (int)HttpStatusCode.UnprocessableEntity =>
                WebSearchResponse.Failed(WebSearchStatus.Rejected, $"Tavily refused the search request (HTTP {status})."),
            _ => WebSearchResponse.Failed(WebSearchStatus.Unavailable, $"Tavily is unavailable right now (HTTP {status}).")
        };
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? retry = response.Headers.RetryAfter;

        if (retry?.Delta is { } delta)
        {
            return delta;
        }

        return retry?.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }

    /// <summary>
    /// The content as text, or <see langword="null"/> if it is longer than <see cref="MAX_RESPONSE_BYTES"/>.
    /// </summary>
    private static async Task<string?> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[16 * 1024];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MAX_RESPONSE_BYTES)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string? Text(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int? Number(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && number >= 0 && number <= int.MaxValue
            ? (int)Math.Ceiling(number)
            : null;
    }

    /// <summary>
    /// The date of a published-date text, which Tavily gives in more than one format.
    /// </summary>
    internal static DateOnly? Date(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            return DateOnly.FromDateTime(parsed.UtcDateTime);
        }

        return text.Length >= 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date) ? date : null;
    }

    private sealed record Outcome(string? Body, WebSearchResponse? Failure);
}
