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
    private bool _reopened;

    public ServiceProvider Services { get; }

    /// <summary>
    /// The directory holding the database.
    /// </summary>
    public string DirectoryPath => _directory;

    private TestDatabase(string directory, ServiceProvider services)
    {
        _directory = directory;
        Services = services;
    }

    /// <param name="testContext">The running test's context.</param>
    /// <param name="configure">Optionally replaces registrations after the tiers are registered.</param>
    public static Task<TestDatabase> CreateAsync(TestContext testContext, Action<IServiceCollection>? configure = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"HomiliaMessenger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        return OpenAsync(directory, testContext, configure);
    }

    /// <summary>
    /// Opens the application database already in <paramref name="directory"/>, as the application would at startup,
    /// migrating it if needed. The returned database takes over the directory, and deletes it on disposal.
    /// </summary>
    public static Task<TestDatabase> OpenExistingAsync(string directory, TestContext testContext, Action<IServiceCollection>? configure = null)
    {
        return OpenAsync(directory, testContext, configure);
    }

    /// <summary>
    /// Closes this database and opens it again, as a restart of the application would. The returned database takes over
    /// the directory, and deletes it on disposal.
    /// </summary>
    public async Task<TestDatabase> ReopenAsync(TestContext testContext, Action<IServiceCollection>? configure = null)
    {
        Close();
        _reopened = true;

        return await OpenAsync(_directory, testContext, configure);
    }

    private static async Task<TestDatabase> OpenAsync(string directory, TestContext testContext, Action<IServiceCollection>? configure)
    {
        ServiceCollection services = new();
        services.RegisterInternalDataServices(directory).RegisterInternalBusinessServices();
        configure?.Invoke(services);
        ServiceProvider provider = services.BuildServiceProvider();
        await provider.InitializeApplicationDataAsync(testContext.CancellationToken);

        return new TestDatabase(directory, provider);
    }

    public void Dispose()
    {
        if (_reopened)
        {
            return;
        }

        Close();
        Directory.Delete(_directory, recursive: true);
    }

    private void Close()
    {
        // Pooled SQLite connections keep the file open on Windows until the pool is cleared. Clear only this database's
        // pool: clearing all pools would dispose connections that tests running in parallel are using.
        using (SqliteConnection connection = Services.GetRequiredService<IApplicationDatabase>().OpenConnectionAsync().GetAwaiter().GetResult())
        {
            SqliteConnection.ClearPool(connection);
        }

        Services.Dispose();
    }
}
