using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.WebSearch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.WebSearch;

[TestClass]
public sealed class WebSearchServiceTests
{
    private static readonly DateTimeOffset NOW = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly PERIOD = new(2026, 11, 1);
    private static readonly IReadOnlyList<ChatMessage> CURRENT_QUESTION = [User("Is the new MonoGame version out yet?")];
    private static readonly IReadOnlyCollection<string> NAMES = ["Walt", "Aaron"];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task LookUp_SearchesWhenEnabledWithAKeyAndCountsTheCredit()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new();
        WebSearchService service = await CreateAsync(database, provider);

        WebLookup? lookup = await LookUpAsync(service, CURRENT_QUESTION);

        Assert.IsNotNull(lookup);
        Assert.AreEqual("MonoGame 3.8.4 released", lookup.Sources[0].Title);
        Assert.AreEqual(1, provider.Searches);
        Assert.IsTrue(provider.Requests.Single().TimeSensitive);
        Assert.AreEqual(1, await Store(database).GetCreditsUsedAsync(PERIOD, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task LookUp_IsOffUntilTheUserTurnsItOn()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new();
        WebSearchService service = await CreateAsync(database, provider, enable: false);

        Assert.IsNull(await LookUpAsync(service, CURRENT_QUESTION));
        Assert.AreEqual(0, provider.Searches);
        Assert.IsFalse((await service.GetOverviewAsync(TestContext.CancellationToken)).Settings.Enabled);
    }

    [TestMethod]
    public async Task LookUp_DoesNothingWithoutAKeyOrWithTavilyOff()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new();
        WebSearchService noKey = await CreateAsync(database, provider, key: null);
        WebSearchService tavilyOff = await CreateAsync(database, provider, settings: new WebSearchSettings(true, false));

        Assert.IsNull(await LookUpAsync(noKey, CURRENT_QUESTION));
        Assert.IsNull(await LookUpAsync(tavilyOff, CURRENT_QUESTION));
        Assert.AreEqual(0, provider.Searches);
    }

    [TestMethod]
    public async Task LookUp_DoesNotSearchForOrdinaryConversation()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new();
        WebSearchService service = await CreateAsync(database, provider);
        int modelCalls = 0;

        WebLookup? lookup = await service.LookUpForReplyAsync([User("How was your day?")], NAMES, (_, _) =>
        {
            modelCalls++;
            return Task.FromResult(AgentReply.Success("YES"));
        }, "model", null, TestContext.CancellationToken);

        Assert.IsNull(lookup);
        Assert.AreEqual(0, provider.Searches);
        Assert.AreEqual(0, modelCalls);
    }

    [TestMethod]
    [DataRow("YES", 1)]
    [DataRow("NO", 0)]
    [DataRow("Well, I think it might", 0)]
    public async Task LookUp_AsksTheModelOnlyWhenUncertain(string answer, int searches)
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new();
        WebSearchService service = await CreateAsync(database, provider);
        List<AgentRequest> asked = [];

        await service.LookUpForReplyAsync([User("Have you played Hollow Knight?")], NAMES, (request, _) =>
        {
            asked.Add(request);
            return Task.FromResult(AgentReply.Success(answer));
        }, "model", null, TestContext.CancellationToken);

        Assert.HasCount(1, asked);
        Assert.IsTrue(asked[0].Precise);
        Assert.AreEqual(searches, provider.Searches);
    }

    [TestMethod]
    public async Task LookUp_DoesNotSearchWhenTheModelDecisionFails()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new();
        WebSearchService service = await CreateAsync(database, provider);

        WebLookup? unavailable = await service.LookUpForReplyAsync([User("Have you played Hollow Knight?")], NAMES,
            (_, _) => Task.FromResult(AgentReply.Unavailable("Model missing.")), "model", null, TestContext.CancellationToken);
        WebLookup? throws = await service.LookUpForReplyAsync([User("Have you played Hollow Knight?")], NAMES,
            (_, _) => throw new InvalidOperationException("Model crashed."), "model", null, TestContext.CancellationToken);

        Assert.IsNull(unavailable);
        Assert.IsNull(throws);
        Assert.AreEqual(0, provider.Searches);
    }

    [TestMethod]
    public async Task LookUp_SkipsTheModelWhenNoSearchCouldFollow()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new();
        WebSearchService service = await CreateAsync(database, provider, settings: new WebSearchSettings(true, true, 0));
        int modelCalls = 0;

        await service.LookUpForReplyAsync([User("Have you played Hollow Knight?")], NAMES, (_, _) =>
        {
            modelCalls++;
            return Task.FromResult(AgentReply.Success("YES"));
        }, "model", null, TestContext.CancellationToken);

        Assert.AreEqual(0, modelCalls);
    }

    [TestMethod]
    public async Task LookUp_StopsAtTheLocalBudget()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new();
        WebSearchService service = await CreateAsync(database, provider, settings: new WebSearchSettings(true, true, 2));

        await LookUpAsync(service, [User("Is the new MonoGame version out yet?")]);
        await LookUpAsync(service, [User("Is the new Avalonia version out yet?")]);
        WebLookup? third = await LookUpAsync(service, [User("Is the new Godot version out yet?")]);
        WebSearchOverview overview = await service.GetOverviewAsync(TestContext.CancellationToken);

        Assert.IsNull(third);
        Assert.AreEqual(2, provider.Searches);
        Assert.AreEqual(2, overview.CreditsUsed);
        Assert.AreEqual(0, overview.CreditsRemaining);
        Assert.IsFalse(overview.IsReady);
    }

    [TestMethod]
    public async Task LookUp_StopsAfterTheKeyIsRejectedAndRefundsTheCredit()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new() { Response = WebSearchResponse.Failed(WebSearchStatus.Unauthorized, "Tavily rejected the API key.") };
        WebSearchService service = await CreateAsync(database, provider);

        Assert.IsNull(await LookUpAsync(service, [User("Is the new MonoGame version out yet?")]));
        Assert.IsNull(await LookUpAsync(service, [User("Is the new Avalonia version out yet?")]));
        WebSearchOverview overview = await service.GetOverviewAsync(TestContext.CancellationToken);

        Assert.AreEqual(1, provider.Searches);
        Assert.IsTrue(overview.State.KeyRejected);
        Assert.AreEqual(0, overview.CreditsUsed);

        // A new key is tried again; the credits counted are kept.
        provider.Response = new FakeSearchProvider().Response;
        await service.SetTavilyKeyAsync("tvly-new", TestContext.CancellationToken);
        Assert.IsNotNull(await LookUpAsync(service, [User("Is the new Avalonia version out yet?")]));
        Assert.AreEqual(2, provider.Searches);
    }

    [TestMethod]
    public async Task LookUp_PausesUntilTheNextPeriodWhenTheQuotaIsUsedUp()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new() { Response = WebSearchResponse.Failed(WebSearchStatus.QuotaExceeded, "Plan used up.") };
        ManualClock clock = new(NOW);
        WebSearchService service = await CreateAsync(database, provider, clock: clock);

        await LookUpAsync(service, [User("Is the new MonoGame version out yet?")]);
        await LookUpAsync(service, [User("Is the new Avalonia version out yet?")]);
        WebSearchOverview paused = await service.GetOverviewAsync(TestContext.CancellationToken);

        Assert.AreEqual(1, provider.Searches);
        Assert.AreEqual(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), paused.State.PausedUntilUtc);
        Assert.AreEqual(0, paused.CreditsUsed);

        provider.Response = new FakeSearchProvider().Response;
        clock.Now = new DateTimeOffset(2026, 12, 1, 0, 0, 1, TimeSpan.Zero);
        Assert.IsNotNull(await LookUpAsync(service, [User("Is the new Avalonia version out yet?")]));
    }

    [TestMethod]
    public async Task LookUp_PausesForTheRateLimit()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new() { Response = WebSearchResponse.Failed(WebSearchStatus.RateLimited, "Slow down.", TimeSpan.FromSeconds(30)) };
        ManualClock clock = new(NOW);
        WebSearchService service = await CreateAsync(database, provider, clock: clock);

        await LookUpAsync(service, [User("Is the new MonoGame version out yet?")]);
        await LookUpAsync(service, [User("Is the new Avalonia version out yet?")]);

        Assert.AreEqual(1, provider.Searches);
        Assert.AreEqual(NOW.AddSeconds(30), (await service.GetOverviewAsync(TestContext.CancellationToken)).State.PausedUntilUtc);

        clock.Now = NOW.AddSeconds(31);
        await LookUpAsync(service, [User("Is the new Avalonia version out yet?")]);
        Assert.AreEqual(2, provider.Searches);
    }

    [TestMethod]
    [DataRow(WebSearchStatus.Unavailable)]
    [DataRow(WebSearchStatus.InvalidResponse)]
    public async Task LookUp_KeepsTheCreditCountedWhenTheOutcomeIsUnknown(WebSearchStatus status)
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new() { Response = WebSearchResponse.Failed(status, "Something went wrong.") };
        WebSearchService service = await CreateAsync(database, provider);

        Assert.IsNull(await LookUpAsync(service, CURRENT_QUESTION));
        WebSearchOverview overview = await service.GetOverviewAsync(TestContext.CancellationToken);

        Assert.AreEqual(1, overview.CreditsUsed);
        Assert.AreEqual("Something went wrong.", overview.State.Problem);
        Assert.IsFalse(overview.State.KeyRejected);
    }

    [TestMethod]
    public async Task LookUp_SurvivesAProviderThatThrows()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new() { Throws = true };
        WebSearchService service = await CreateAsync(database, provider);

        Assert.IsNull(await LookUpAsync(service, CURRENT_QUESTION));
        Assert.AreEqual(1, (await service.GetOverviewAsync(TestContext.CancellationToken)).CreditsUsed);
    }

    [TestMethod]
    public async Task LookUp_CorrectsTheCountToWhatTheProviderReported()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new();
        provider.Response = provider.Response with { CreditsUsed = 2 };
        WebSearchService service = await CreateAsync(database, provider);

        await LookUpAsync(service, CURRENT_QUESTION);

        Assert.AreEqual(2, (await service.GetOverviewAsync(TestContext.CancellationToken)).CreditsUsed);
    }

    [TestMethod]
    public async Task LookUp_UsesTheCacheWithoutSpendingCreditsUntilItExpires()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new();
        ManualClock clock = new(NOW);
        WebSearchService service = await CreateAsync(database, provider, clock: clock);

        WebLookup? first = await LookUpAsync(service, CURRENT_QUESTION);
        WebLookup? second = await LookUpAsync(service, [User("is the NEW MonoGame version out yet???")]);

        Assert.AreEqual(1, provider.Searches);
        Assert.AreEqual(first!.Sources[0], second!.Sources[0]);
        Assert.AreEqual(1, (await service.GetOverviewAsync(TestContext.CancellationToken)).CreditsUsed);

        // Time-sensitive results expire sooner than others.
        clock.Now = NOW + WebSearchService.TIME_SENSITIVE_CACHE_LIFETIME + TimeSpan.FromMinutes(1);
        await LookUpAsync(service, CURRENT_QUESTION);
        Assert.AreEqual(2, provider.Searches);
    }

    [TestMethod]
    public async Task LookUp_SharesCachedResultsAcrossContactsEvenOverBudget()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new();
        WebSearchService service = await CreateAsync(database, provider, settings: new WebSearchSettings(true, true, 1));

        await service.LookUpForReplyAsync(CURRENT_QUESTION, ["Walt"], NoModel, "model", null, TestContext.CancellationToken);
        WebLookup? other = await service.LookUpForReplyAsync(CURRENT_QUESTION, ["Jen"], NoModel, "model", null, TestContext.CancellationToken);

        Assert.IsNotNull(other);
        Assert.AreEqual(1, provider.Searches);
    }

    [TestMethod]
    public async Task LookUp_SharesAnIdenticalSearchInProgress()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new() { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        WebSearchService service = await CreateAsync(database, provider);

        Task<WebLookup?> first = LookUpAsync(service, CURRENT_QUESTION);
        Task<WebLookup?> second = LookUpAsync(service, CURRENT_QUESTION);
        await WaitUntilAsync(() => provider.Searches > 0);
        provider.Gate.SetResult();

        Assert.IsNotNull(await first);
        Assert.IsNotNull(await second);
        Assert.AreEqual(1, provider.Searches);
    }

    [TestMethod]
    public async Task LookUp_ThrowsWhenCanceledAndTheSearchStillFinishesForLater()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new() { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        WebSearchService service = await CreateAsync(database, provider);
        using CancellationTokenSource cancel = new();

        Task<WebLookup?> canceled = service.LookUpForReplyAsync(CURRENT_QUESTION, NAMES, NoModel, "model", null, cancel.Token);
        await WaitUntilAsync(() => provider.Searches > 0);
        cancel.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => canceled);

        provider.Gate.SetResult();
        await WaitUntilAsync(() => Store(database).GetCachedAsync(WebSearchService.CacheKey(SearchDecision.Assess(CURRENT_QUESTION, NAMES, DateOnly.FromDateTime(NOW.UtcDateTime)).Query!, true), NOW, TestContext.CancellationToken).Result is not null);
        Assert.IsNotNull(await LookUpAsync(service, CURRENT_QUESTION));
        Assert.AreEqual(1, provider.Searches);
    }

    [TestMethod]
    public async Task LookUp_ReportsSearchingOnlyWhenItSends()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new();
        WebSearchService service = await CreateAsync(database, provider);
        int reports = 0;

        await service.LookUpForReplyAsync(CURRENT_QUESTION, NAMES, NoModel, "model", () => reports++, TestContext.CancellationToken);
        await service.LookUpForReplyAsync([User("How are you?")], NAMES, NoModel, "model", () => reports++, TestContext.CancellationToken);

        Assert.AreEqual(1, reports);
    }

    [TestMethod]
    public async Task Usage_PersistsAcrossRestartsAndRollsOverWithThePeriod()
    {
        TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        ManualClock clock = new(NOW);
        WebSearchService service = await CreateAsync(database, new FakeSearchProvider(), clock: clock, settings: new WebSearchSettings(true, true, 500, 10));

        await LookUpAsync(service, CURRENT_QUESTION);
        using TestDatabase reopened = await database.ReopenAsync(TestContext);
        WebSearchService restarted = new(Store(reopened), new FakeSearchProvider(), new MemoryCredentialStore("tvly-test"), clock);
        WebSearchOverview overview = await restarted.GetOverviewAsync(TestContext.CancellationToken);

        Assert.AreEqual(new DateOnly(2026, 11, 10), overview.PeriodStart);
        Assert.AreEqual(1, overview.CreditsUsed);

        // Editing the key keeps the count.
        await restarted.SetTavilyKeyAsync("tvly-other", TestContext.CancellationToken);
        Assert.AreEqual(1, (await restarted.GetOverviewAsync(TestContext.CancellationToken)).CreditsUsed);

        clock.Now = new DateTimeOffset(2026, 12, 10, 0, 0, 0, TimeSpan.Zero);
        WebSearchOverview next = await restarted.GetOverviewAsync(TestContext.CancellationToken);
        Assert.AreEqual(new DateOnly(2026, 12, 10), next.PeriodStart);
        Assert.AreEqual(0, next.CreditsUsed);
    }

    [TestMethod]
    [DataRow(2026, 11, 15, 1, 2026, 11, 1)]
    [DataRow(2026, 11, 15, 15, 2026, 11, 15)]
    [DataRow(2026, 11, 14, 15, 2026, 10, 15)]
    [DataRow(2026, 1, 5, 28, 2025, 12, 28)]
    public void PeriodStart_IsTheMostRecentResetDay(int year, int month, int day, int resetDay, int startYear, int startMonth, int startDay)
    {
        Assert.AreEqual(new DateOnly(startYear, startMonth, startDay), WebSearchService.PeriodStart(new DateOnly(year, month, day), resetDay));
    }

    [TestMethod]
    public async Task Reserve_NeverOverspendsUnderConcurrency()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        IWebSearchStore store = Store(database);

        bool[] reserved = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => store.TryReserveCreditsAsync(PERIOD, 1, 7, TestContext.CancellationToken))));

        Assert.AreEqual(7, reserved.Count(value => value));
        Assert.AreEqual(7, await store.GetCreditsUsedAsync(PERIOD, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task CheckKey_RaisesTheCountToWhatTavilyReports()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new() { KeyCheck = new WebSearchKeyCheck(WebSearchStatus.Success, 42, 100, 1000) };
        WebSearchService service = await CreateAsync(database, provider);

        WebSearchKeyCheck check = await service.CheckTavilyKeyAsync(TestContext.CancellationToken);

        Assert.AreEqual(WebSearchStatus.Success, check.Status);
        Assert.AreEqual(42, (await service.GetOverviewAsync(TestContext.CancellationToken)).CreditsUsed);
    }

    [TestMethod]
    public async Task SaveSettings_RejectsInvalidValues()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        WebSearchService service = await CreateAsync(database, new FakeSearchProvider());

        Assert.IsFalse((await service.SaveSettingsAsync(new WebSearchSettings(true, true, -1), TestContext.CancellationToken)).IsValid);
        Assert.IsFalse((await service.SaveSettingsAsync(new WebSearchSettings(true, true, 10, 31), TestContext.CancellationToken)).IsValid);
        Assert.AreEqual(WebSearchSettings.DEFAULT_MONTHLY_CREDIT_BUDGET, (await service.GetOverviewAsync(TestContext.CancellationToken)).Settings.MonthlyCreditBudget);
    }

    internal static async Task<WebSearchService> CreateAsync(TestDatabase database, FakeSearchProvider provider, bool enable = true, string? key = "tvly-test", WebSearchSettings? settings = null, ManualClock? clock = null)
    {
        IWebSearchStore store = Store(database);
        await store.SaveSettingsAsync(settings ?? new WebSearchSettings(enable));

        return new WebSearchService(store, provider, new MemoryCredentialStore(key), clock ?? new ManualClock(NOW));
    }

    private Task<WebLookup?> LookUpAsync(WebSearchService service, IReadOnlyList<ChatMessage> recent)
    {
        return service.LookUpForReplyAsync(recent, NAMES, NoModel, "model", null, TestContext.CancellationToken);
    }

    private static Task<AgentReply> NoModel(AgentRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(AgentReply.Success("NO"));
    }

    private static IWebSearchStore Store(TestDatabase database)
    {
        return database.Services.GetRequiredService<IWebSearchStore>();
    }

    private static ChatMessage User(string text)
    {
        return new ChatMessage { Id = MessageId.New(), Sender = MessageSender.User, Text = text };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(25);
        }

        Assert.IsTrue(condition(), "Timed out waiting.");
    }
}
