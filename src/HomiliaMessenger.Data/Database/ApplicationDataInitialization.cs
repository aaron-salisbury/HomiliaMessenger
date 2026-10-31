using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Data.Database;

public static class ApplicationDataInitialization
{
    public static Task InitializeApplicationDataAsync(this IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return serviceProvider
            .GetRequiredService<ApplicationDatabaseInitializer>()
            .InitializeAsync(cancellationToken);
    }
}
