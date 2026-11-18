using Dapper;
using HomiliaMessenger.Business.Modules.WebSearch;
using HomiliaMessenger.Data.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.WebSearch;

[TestClass]
public sealed class WebSearchMigrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task StartupRepairsMissingQuotaColumnWithoutResettingWebSearch()
    {
        TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        IWebSearchStore store = database.Services.GetRequiredService<IWebSearchStore>();
        await store.SaveSettingsAsync(new WebSearchSettings(true, true, 250, 12), TestContext.CancellationToken);

        await using (SqliteConnection connection = await database.Services.GetRequiredService<IApplicationDatabase>()
            .OpenConnectionAsync(TestContext.CancellationToken))
        {
            await connection.ExecuteAsync("ALTER TABLE WebSearchSettings DROP COLUMN PausedForQuota");
            await connection.ExecuteAsync("DELETE FROM SchemaMigration WHERE Version = 9");
        }

        using TestDatabase reopened = await database.ReopenAsync(TestContext);
        IWebSearchStore repaired = reopened.Services.GetRequiredService<IWebSearchStore>();
        WebSearchSettings settings = await repaired.GetSettingsAsync(TestContext.CancellationToken);
        await repaired.SaveStateAsync(new WebSearchProviderState(PausedForQuota: true), TestContext.CancellationToken);

        Assert.IsTrue(settings.Enabled);
        Assert.IsTrue(settings.TavilyEnabled);
        Assert.AreEqual(250, settings.MonthlyCreditBudget);
        Assert.AreEqual(12, settings.CreditResetDay);
        Assert.IsTrue((await repaired.GetStateAsync(TestContext.CancellationToken)).PausedForQuota);
    }
}
