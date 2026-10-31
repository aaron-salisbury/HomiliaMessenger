using HomiliaMessenger.Business.Modules.Credentials;
using HomiliaMessenger.Integrations.Credentials;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Runtime.InteropServices;

namespace HomiliaMessenger.Integrations;

public static class DependencyInjection
{
    /// <summary>
    /// Registers internal integrations-tier services.
    /// </summary>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    public static IServiceCollection RegisterInternalIntegrationsServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ICredentialStore>(_ =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new WindowsCredentialStore();
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return new LinuxSecretServiceCredentialStore();
            }

            return new UnsupportedCredentialStore();
        });

        // Agent providers (and later speech providers) that satisfy Business capability contracts register here.

        return services;
    }
}
