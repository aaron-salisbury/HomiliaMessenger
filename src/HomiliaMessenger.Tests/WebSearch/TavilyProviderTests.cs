using HomiliaMessenger.Business.Modules.Credentials;
using HomiliaMessenger.Business.Modules.WebSearch;
using HomiliaMessenger.Integrations.WebSearch;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.WebSearch;

[TestClass]
public sealed class TavilyProviderTests
{
    private const string KEY = "tvly-secret-key";

    private const string RESULTS = """
        {
          "query": "MonoGame latest release",
          "answer": null,
          "images": [],
          "results": [
            { "title": "MonoGame 3.8.4", "url": "https://monogame.net/blog/3.8.4", "content": "MonoGame 3.8.4 is out.", "score": 0.9, "published_date": "2026-10-01T08:00:00Z" },
            { "title": "Not a web page", "url": "javascript:alert(1)", "content": "Ignore this.", "score": 0.5 },
            { "title": "GitHub", "url": "https://github.com/MonoGame/MonoGame", "content": "Releases.", "score": 0.4, "published_date": "Thu, 01 Oct 2026 00:00:00 GMT" }
          ],
          "response_time": 1.2,
          "usage": { "credits": 1 },
          "request_id": "abc"
        }
        """;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Search_SendsABasicSearchAndReadsTheResults()
    {
        FakeHttp http = new() { Respond = _ => Json(HttpStatusCode.OK, RESULTS) };

        WebSearchResponse response = await Provider(http).SearchAsync(new WebSearchRequest("MonoGame latest release", true), new CredentialMaterial(KEY), TestContext.CancellationToken);

        Assert.AreEqual(WebSearchStatus.Success, response.Status);
        Assert.HasCount(2, response.Results);
        Assert.AreEqual(new WebSearchResult("MonoGame 3.8.4", "https://monogame.net/blog/3.8.4", "MonoGame 3.8.4 is out.", new DateOnly(2026, 10, 1)), response.Results[0]);
        Assert.AreEqual(new DateOnly(2026, 10, 1), response.Results[1].PublishedAt);
        Assert.AreEqual(1, response.CreditsUsed);

        (HttpRequestMessage request, string? body) = http.Requests[0];
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual(TavilyWebSearchProvider.SEARCH_URL, request.RequestUri!.AbsoluteUri);
        Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
        Assert.AreEqual(KEY, request.Headers.Authorization.Parameter);

        using JsonDocument sent = JsonDocument.Parse(body!);
        Assert.AreEqual("MonoGame latest release", sent.RootElement.GetProperty("query").GetString());
        Assert.AreEqual("basic", sent.RootElement.GetProperty("search_depth").GetString());
        Assert.IsFalse(sent.RootElement.GetProperty("auto_parameters").GetBoolean());
        Assert.IsFalse(sent.RootElement.GetProperty("include_answer").GetBoolean());
        Assert.IsFalse(sent.RootElement.GetProperty("include_raw_content").GetBoolean());
        Assert.DoesNotContain(KEY, body!);
    }

    [TestMethod]
    [DataRow(401, WebSearchStatus.Unauthorized)]
    [DataRow(403, WebSearchStatus.Unauthorized)]
    [DataRow(429, WebSearchStatus.RateLimited)]
    [DataRow(432, WebSearchStatus.QuotaExceeded)]
    [DataRow(433, WebSearchStatus.QuotaExceeded)]
    [DataRow(400, WebSearchStatus.Rejected)]
    [DataRow(422, WebSearchStatus.Rejected)]
    [DataRow(500, WebSearchStatus.Unavailable)]
    [DataRow(503, WebSearchStatus.Unavailable)]
    public async Task Search_ReportsErrorsAsAStatusWithoutTheKey(int status, WebSearchStatus expected)
    {
        FakeHttp http = new() { Respond = _ => Json((HttpStatusCode)status, "{\"detail\":{\"error\":\"" + KEY + "\"}}") };

        WebSearchResponse response = await Provider(http).SearchAsync(new WebSearchRequest("query", false), new CredentialMaterial(KEY), TestContext.CancellationToken);

        Assert.AreEqual(expected, response.Status);
        Assert.IsEmpty(response.Results);
        Assert.IsNotNull(response.Problem);
        Assert.DoesNotContain(KEY, response.Problem);
    }

    [TestMethod]
    public async Task Search_ReadsRetryAfterOnARateLimit()
    {
        FakeHttp http = new()
        {
            Respond = _ =>
            {
                HttpResponseMessage response = Json(HttpStatusCode.TooManyRequests, "{}");
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
                return response;
            }
        };

        WebSearchResponse response = await Provider(http).SearchAsync(new WebSearchRequest("query", false), new CredentialMaterial(KEY), TestContext.CancellationToken);

        Assert.AreEqual(TimeSpan.FromSeconds(42), response.RetryAfter);
    }

    [TestMethod]
    [DataRow("not json at all")]
    [DataRow("<html><body>Captcha</body></html>")]
    [DataRow("{\"query\":\"x\"}")]
    [DataRow("{\"results\":\"nope\"}")]
    [DataRow("[]")]
    public async Task Search_RejectsMalformedResponses(string body)
    {
        FakeHttp http = new() { Respond = _ => Json(HttpStatusCode.OK, body) };

        WebSearchResponse response = await Provider(http).SearchAsync(new WebSearchRequest("query", false), new CredentialMaterial(KEY), TestContext.CancellationToken);

        Assert.AreEqual(WebSearchStatus.InvalidResponse, response.Status);
    }

    [TestMethod]
    public async Task Search_RejectsAnOversizedResponse()
    {
        string huge = "{\"results\":[{\"title\":\"" + new string('a', TavilyWebSearchProvider.MAX_RESPONSE_BYTES) + "\",\"url\":\"https://a.example\"}]}";
        FakeHttp http = new() { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new System.IO.MemoryStream(Encoding.UTF8.GetBytes(huge))) } };

        WebSearchResponse response = await Provider(http).SearchAsync(new WebSearchRequest("query", false), new CredentialMaterial(KEY), TestContext.CancellationToken);

        Assert.AreEqual(WebSearchStatus.InvalidResponse, response.Status);
    }

    [TestMethod]
    public async Task Search_ReportsANetworkFailureAsUnavailable()
    {
        FakeHttp http = new() { Respond = _ => throw new HttpRequestException("No route to host.") };

        WebSearchResponse response = await Provider(http).SearchAsync(new WebSearchRequest("query", false), new CredentialMaterial(KEY), TestContext.CancellationToken);

        Assert.AreEqual(WebSearchStatus.Unavailable, response.Status);
    }

    [TestMethod]
    public async Task Search_ThrowsWhenTheCallerCancels()
    {
        FakeHttp http = new() { Delay = TimeSpan.FromSeconds(30), Respond = _ => Json(HttpStatusCode.OK, RESULTS) };
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(() => Provider(http).SearchAsync(new WebSearchRequest("query", false), new CredentialMaterial(KEY), cancel.Token));
    }

    [TestMethod]
    public async Task CheckKey_ReadsUsageWithoutSearching()
    {
        FakeHttp http = new() { Respond = _ => Json(HttpStatusCode.OK, """{"key":{"usage":150,"search_usage":120,"limit":null},"account":{"current_plan":"Researcher","plan_usage":300,"plan_limit":1000}}""") };

        WebSearchKeyCheck check = await Provider(http).CheckKeyAsync(new CredentialMaterial(KEY), TestContext.CancellationToken);

        Assert.AreEqual(WebSearchStatus.Success, check.Status);
        Assert.AreEqual(120, check.KeyCreditsUsed);
        Assert.AreEqual(300, check.PlanCreditsUsed);
        Assert.AreEqual(1000, check.PlanCreditLimit);
        Assert.AreEqual(HttpMethod.Get, http.Requests[0].Request.Method);
        Assert.AreEqual(TavilyWebSearchProvider.USAGE_URL, http.Requests[0].Request.RequestUri!.AbsoluteUri);
    }

    [TestMethod]
    public async Task CheckKey_ReportsARejectedKey()
    {
        FakeHttp http = new() { Respond = _ => Json(HttpStatusCode.Unauthorized, "{}") };

        WebSearchKeyCheck check = await Provider(http).CheckKeyAsync(new CredentialMaterial(KEY), TestContext.CancellationToken);

        Assert.AreEqual(WebSearchStatus.Unauthorized, check.Status);
    }

    private static TavilyWebSearchProvider Provider(FakeHttp http)
    {
        return new TavilyWebSearchProvider(http, NullLogger<TavilyWebSearchProvider>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
