using FluentValidation.Results;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.WebSearch;
using HomiliaMessenger.Presentation.Desktop.Settings;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using HomiliaMessenger.Tests.Speech;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.WebSearch;

/// <summary>
/// Outcomes that arrive out of order must never undo a newer limit, key, or setting.
/// </summary>
[TestClass]
public sealed class WebSearchRaceTests
{
    private static readonly DateTimeOffset NOW = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<ChatMessage> QUESTION = [new ChatMessage { Id = MessageId.New(), Sender = MessageSender.User, Text = "Is the new MonoGame version out yet?" }];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ASuccessDoesNotClearAPauseRecordedMeanwhile()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new() { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        WebSearchService service = await WebSearchServiceTests.CreateAsync(database, provider);
        IWebSearchStore store = database.Services.GetRequiredService<IWebSearchStore>();

        Task<WebLookup?> search = LookUpAsync(service);
        await WaitUntilAsync(() => provider.Searches > 0);
        WebSearchProviderState paused = new(false, NOW.AddMinutes(5), "Rate limited.");
        await store.SaveStateAsync(paused, TestContext.CancellationToken);
        provider.Gate.SetResult();
        await search;

        Assert.AreEqual(paused, await store.GetStateAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ARejectionOfTheOldKeyDoesNotRejectANewOne()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new()
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Response = WebSearchResponse.Failed(WebSearchStatus.Unauthorized, "Rejected.")
        };
        WebSearchService service = await WebSearchServiceTests.CreateAsync(database, provider);

        Task<WebLookup?> search = LookUpAsync(service);
        await WaitUntilAsync(() => provider.Searches > 0);
        await service.SetTavilyKeyAsync("tvly-new", TestContext.CancellationToken);
        provider.Gate.SetResult();
        await search;
        WebSearchOverview overview = await service.GetOverviewAsync(TestContext.CancellationToken);

        Assert.IsFalse(overview.State.KeyRejected);
        // The old key's refund is not given, in case the count was since raised for the new key.
        Assert.AreEqual(1, overview.CreditsUsed);
    }

    [TestMethod]
    [DataRow(WebSearchStatus.QuotaExceeded)]
    [DataRow(WebSearchStatus.RateLimited)]
    public async Task ALimitReportedForTheOldKeyDoesNotPauseANewOne(WebSearchStatus status)
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new()
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Response = WebSearchResponse.Failed(status, "Limit reached.", TimeSpan.FromMinutes(10))
        };
        WebSearchService service = await WebSearchServiceTests.CreateAsync(database, provider);

        Task<WebLookup?> search = LookUpAsync(service);
        await WaitUntilAsync(() => provider.Searches > 0);
        await service.SetTavilyKeyAsync("tvly-new", TestContext.CancellationToken);
        await service.CheckTavilyKeyAsync(TestContext.CancellationToken);
        provider.Gate.SetResult();
        await search;
        WebSearchOverview overview = await service.GetOverviewAsync(TestContext.CancellationToken);

        Assert.IsNull(overview.State.PausedUntilUtc);
        Assert.IsNull(overview.State.Problem);
        // The old key's refund is not given, in case the count was since raised for the new key.
        Assert.AreEqual(1, overview.CreditsUsed);
    }

    [TestMethod]
    public async Task TheLatestSettingsChangeWinsOverASlowerEarlierSave()
    {
        SlowSaves webSearch = new();
        SettingsViewModel settings = new(new ThemeService(new DesktopSettings(Path.GetTempPath())), new SpeechSettingsTests.EmptyLocalModels(), webSearch: webSearch);
        await settings.InitializeAsync();

        // The first save is still in progress when the user changes their mind.
        settings.WebSearchEnabled = true;
        await WaitUntilAsync(() => webSearch.Saving);
        settings.WebSearchEnabled = false;
        webSearch.FirstSave.SetResult();
        await WaitUntilAsync(() => webSearch.Saved.Count == 2 && !webSearch.Saving);
        await Task.Delay(100, TestContext.CancellationToken);

        Assert.IsFalse(webSearch.Saved[1].Enabled);
        Assert.IsFalse(webSearch.Current.Enabled);
        Assert.IsFalse(settings.WebSearchEnabled);
    }

    [TestMethod]
    public async Task ARefreshReadBeforeANewerChangeDoesNotPutTheOldValueBack()
    {
        SlowSaves webSearch = new();
        webSearch.FirstSave.SetResult();
        SettingsViewModel settings = new(new ThemeService(new DesktopSettings(Path.GetTempPath())), new SpeechSettingsTests.EmptyLocalModels(), webSearch: webSearch);
        await settings.InitializeAsync();
        webSearch.OverviewGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int overviews = webSearch.Overviews;

        // The refresh after turning search on reads "on", and is still pending when the user turns it off.
        settings.WebSearchEnabled = true;
        await WaitUntilAsync(() => webSearch.Overviews > overviews);
        settings.WebSearchEnabled = false;
        webSearch.OverviewGate.SetResult();
        await WaitUntilAsync(() => webSearch.Saved.Count == 2);
        await Task.Delay(100, TestContext.CancellationToken);

        Assert.IsFalse(webSearch.Saved[1].Enabled);
        Assert.IsFalse(webSearch.Current.Enabled);
        Assert.IsFalse(settings.WebSearchEnabled);
    }

    [TestMethod]
    public async Task ACheckThatDoesNotReportUsageKeepsAQuotaPause()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new()
        {
            Response = WebSearchResponse.Failed(WebSearchStatus.QuotaExceeded, "Used up."),
            KeyCheck = new WebSearchKeyCheck(WebSearchStatus.Success)
        };
        WebSearchService service = await WebSearchServiceTests.CreateAsync(database, provider);

        await LookUpAsync(service);
        await service.CheckTavilyKeyAsync(TestContext.CancellationToken);
        WebSearchOverview overview = await service.GetOverviewAsync(TestContext.CancellationToken);

        Assert.IsNotNull(overview.State.PausedUntilUtc);
        Assert.IsTrue(overview.State.PausedForQuota);
    }

    [TestMethod]
    public async Task UsageReportedForTheOldKeyIsNotCountedForANewOne()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new()
        {
            CheckGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            KeyCheck = new WebSearchKeyCheck(WebSearchStatus.Success, 999, 999, 1000)
        };
        WebSearchService service = await WebSearchServiceTests.CreateAsync(database, provider);

        Task<WebSearchKeyCheck> check = service.CheckTavilyKeyAsync(TestContext.CancellationToken);
        await WaitUntilAsync(() => provider.Checks > 0);
        await service.SetTavilyKeyAsync("tvly-new", TestContext.CancellationToken);
        provider.CheckGate.SetResult();
        await check;

        Assert.AreEqual(0, (await service.GetOverviewAsync(TestContext.CancellationToken)).CreditsUsed);
    }

    [TestMethod]
    public async Task CheckingTheKeyLiftsAQuotaPauseButNotARateLimit()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        FakeSearchProvider provider = new() { Response = WebSearchResponse.Failed(WebSearchStatus.RateLimited, "Slow down.", TimeSpan.FromMinutes(10)) };
        WebSearchService service = await WebSearchServiceTests.CreateAsync(database, provider);

        await LookUpAsync(service);
        await service.CheckTavilyKeyAsync(TestContext.CancellationToken);
        WebSearchOverview rateLimited = await service.GetOverviewAsync(TestContext.CancellationToken);

        Assert.AreEqual(NOW.AddMinutes(10), rateLimited.State.PausedUntilUtc);

        using TestDatabase other = await TestDatabase.CreateAsync(TestContext);
        provider.Response = WebSearchResponse.Failed(WebSearchStatus.QuotaExceeded, "Used up.");
        WebSearchService quota = await WebSearchServiceTests.CreateAsync(other, provider);

        await LookUpAsync(quota);
        Assert.IsTrue((await quota.GetOverviewAsync(TestContext.CancellationToken)).State.PausedForQuota);
        await quota.CheckTavilyKeyAsync(TestContext.CancellationToken);

        Assert.IsNull((await quota.GetOverviewAsync(TestContext.CancellationToken)).State.PausedUntilUtc);
    }

    private Task<WebLookup?> LookUpAsync(WebSearchService service)
    {
        return service.LookUpForReplyAsync(QUESTION, ["Walt"], (_, _) => Task.FromResult(AgentReply.Success("NO")), "model", null, TestContext.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(25);
        }

        Assert.IsTrue(condition(), "Timed out waiting.");
    }

    /// <summary>
    /// Settings whose first save waits for <see cref="FirstSave"/>, recording what is saved.
    /// </summary>
    private sealed class SlowSaves : IWebSearchService
    {
        private int _saves;

        public TaskCompletionSource FirstSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WebSearchSettings Current { get; private set; } = new();

        public List<WebSearchSettings> Saved { get; } = [];

        public bool Saving { get; private set; }

        /// <summary>
        /// Waited on before returning an overview, when set, which reflects the settings when it was asked for.
        /// </summary>
        public TaskCompletionSource? OverviewGate { get; set; }

        public int Overviews { get; private set; }

        public async Task<WebSearchOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        {
            WebSearchSettings snapshot = Current;
            Overviews++;

            if (OverviewGate is { } gate)
            {
                await gate.Task;
            }

            return new WebSearchOverview(snapshot, false, new DateOnly(2026, 11, 1), 0, snapshot.MonthlyCreditBudget, new WebSearchProviderState());
        }

        public async Task<ValidationResult> SaveSettingsAsync(WebSearchSettings settings, CancellationToken cancellationToken = default)
        {
            Saving = true;

            if (Interlocked.Increment(ref _saves) == 1)
            {
                await FirstSave.Task;
            }

            Saved.Add(settings);
            Current = settings;
            Saving = false;

            return new ValidationResult();
        }

        public Task SetTavilyKeyAsync(string? apiKey, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<WebSearchKeyCheck> CheckTavilyKeyAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WebSearchKeyCheck(WebSearchStatus.Success));
        }
    }
}
