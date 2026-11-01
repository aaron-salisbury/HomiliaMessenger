using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Data.Database;
using HomiliaMessenger.Data.Database.Migrations;
using HomiliaMessenger.Data.Modules.Contacts;
using HomiliaMessenger.Data.Modules.Conversations;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;

namespace HomiliaMessenger.Data;

public static class DependencyInjection
{
    private const string DATABASE_FILE_NAME = "HomiliaMessenger.db";

    /// <summary>
    /// Registers internal data-tier services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="applicationDataDirectory">An existing directory where the application database is stored.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    public static IServiceCollection RegisterInternalDataServices(this IServiceCollection services, string applicationDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);

        if (!Directory.Exists(applicationDataDirectory))
        {
            throw new DirectoryNotFoundException($"The application data directory '{applicationDataDirectory}' does not exist.");
        }

        string databasePath = Path.Combine(applicationDataDirectory, DATABASE_FILE_NAME);
        services.AddSingleton<IApplicationDatabase>(_ => new ApplicationDatabase(databasePath));
        services.AddSingleton<ApplicationDatabaseInitializer>(provider => new ApplicationDatabaseInitializer(
            provider.GetRequiredService<IApplicationDatabase>(), [new CreateContactsMigration(), new CreateConversationsMigration()]));

        services.AddScoped<IContactStore, SqliteContactStore>();
        services.AddScoped<IConversationStore, SqliteConversationStore>();

        return services;
    }
}
