using HomiliaMessenger.Business;
using HomiliaMessenger.Data;
using HomiliaMessenger.Data.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests;

/// <summary>
/// The data and business tiers over a fresh SQLite database in a temporary directory, deleted on disposal.
/// </summary>
internal sealed class TestDatabase : IDisposable
{
    private readonly string _directory;

    public ServiceProvider Services { get; }

    private TestDatabase(string directory, ServiceProvider services)
    {
        _directory = directory;
        Services = services;
    }

    /// <param name="testContext">The running test's context.</param>
    /// <param name="configure">Optionally replaces registrations after the tiers are registered.</param>
    public static async Task<TestDatabase> CreateAsync(TestContext testContext, Action<IServiceCollection>? configure = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"HomiliaMessenger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        ServiceCollection services = new();
        services.RegisterInternalDataServices(directory).RegisterInternalBusinessServices();
        configure?.Invoke(services);
        ServiceProvider provider = services.BuildServiceProvider();
        await provider.InitializeApplicationDataAsync(testContext.CancellationToken);

        return new TestDatabase(directory, provider);
    }

    public void Dispose()
    {
        Services.Dispose();

        // Pooled SQLite connections keep the file open on Windows until the pool is cleared.
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
