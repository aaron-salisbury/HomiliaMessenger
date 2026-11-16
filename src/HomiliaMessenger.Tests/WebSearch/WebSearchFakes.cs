using HomiliaMessenger.Business.Modules.Credentials;
using HomiliaMessenger.Business.Modules.WebSearch;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.WebSearch;

/// <summary>
/// A credential store held in memory.
/// </summary>
internal sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _values = new();

    public MemoryCredentialStore(string? tavilyKey = null)
    {
        if (tavilyKey is not null)
        {
            _values["tavily-api-key"] = tavilyKey;
        }
    }

    public Task DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default)
    {
        _values.TryRemove(reference.Value, out _);
        return Task.CompletedTask;
    }

    public Task<CredentialMaterial?> GetAsync(CredentialReference reference, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_values.TryGetValue(reference.Value, out string? value) ? new CredentialMaterial(value) : null);
    }

    public Task StoreAsync(CredentialReference reference, CredentialMaterial material, CancellationToken cancellationToken = default)
    {
        _values[reference.Value] = material.Value;
        return Task.CompletedTask;
    }
}

/// <summary>
/// A search provider that answers every search with <see cref="Response"/>, recording what it was asked.
/// </summary>
internal sealed class FakeSearchProvider : IWebSearchProvider
{
    private int _searches;

    public WebSearchResponse Response { get; set; } = new(WebSearchStatus.Success, [new WebSearchResult("MonoGame 3.8.4 released", "https://monogame.net/blog/3.8.4", "MonoGame 3.8.4 adds new platforms.", new DateOnly(2026, 10, 1))], 1);

    public WebSearchKeyCheck KeyCheck { get; set; } = new(WebSearchStatus.Success, 0, 0, 1000);

    /// <summary>
    /// Waited on before answering, when set, so tests can hold a search in progress.
    /// </summary>
    public TaskCompletionSource? Gate { get; set; }

    public bool Throws { get; set; }

    public ConcurrentQueue<WebSearchRequest> Requests { get; } = new();

    public int Searches => _searches;

    public async Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CredentialMaterial apiKey, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _searches);
        Requests.Enqueue(request);

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (Throws)
        {
            throw new InvalidOperationException("Provider exploded.");
        }

        return Response;
    }

    /// <summary>
    /// Waited on before answering a key check, when set.
    /// </summary>
    public TaskCompletionSource? CheckGate { get; set; }

    public int Checks { get; private set; }

    public async Task<WebSearchKeyCheck> CheckKeyAsync(CredentialMaterial apiKey, CancellationToken cancellationToken = default)
    {
        Checks++;

        if (CheckGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return KeyCheck;
    }
}

/// <summary>
/// A clock tests move by hand.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    public ManualClock(DateTimeOffset now)
    {
        Now = now;
    }

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow()
    {
        return Now;
    }
}

/// <summary>
/// An HTTP handler and client factory answering every request with a prepared response, recording the requests.
/// </summary>
internal sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
{
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

    public TimeSpan Delay { get; set; }

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    public HttpClient CreateClient(string name)
    {
        return new HttpClient(this, disposeHandler: false);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        return Respond(request);
    }
}
