using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Credentials;
using HomiliaMessenger.Integrations.Credentials;
using HomiliaMessenger.Integrations.LocalModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace HomiliaMessenger.Integrations;

public static class DependencyInjection
{
    private const string MODELS_DIRECTORY_NAME = "Models";

    /// <summary>
    /// Registers internal integrations-tier services. Local agent models need <see cref="IHttpClientFactory"/> and
    /// logging, registered by the composition root.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="applicationDataDirectory">The directory under which downloaded local models are stored.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    public static IServiceCollection RegisterInternalIntegrationsServices(this IServiceCollection services, string applicationDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);

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
        string modelsDirectory = Path.Combine(applicationDataDirectory, MODELS_DIRECTORY_NAME);
        services.AddSingleton<OnnxModelHost>();
        services.AddSingleton(provider => new LocalModelLibrary(
            modelsDirectory,
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<OnnxModelHost>(),
            provider.GetRequiredService<ILogger<LocalModelLibrary>>()));
        services.AddSingleton<ILocalModelLibrary>(provider => provider.GetRequiredService<LocalModelLibrary>());
        services.AddSingleton<IConversationAgent, OnnxConversationAgent>();

        return services;
    }
}
