using FluentValidation;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.Memories;
using HomiliaMessenger.Business.Modules.Profile;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Reflection;

namespace HomiliaMessenger.Business;

public static class DependencyInjection
{
    /// <summary>
    /// Registers internal business-tier services.
    /// </summary>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    public static IServiceCollection RegisterInternalBusinessServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly(), includeInternalTypes: true);

        services.AddScoped<IContactService, ContactService>();
        services.AddScoped<IUserProfileService, UserProfileService>();

        services.AddSingleton<IConversationAgent, SimulatedAgent>();
        services.AddScoped<IConversationService, ConversationService>();

        // Integrations may register an embedding provider and the composition root other recall limits; without them,
        // memories are recalled by keywords within the default limits.
        services.TryAddSingleton<ITextEmbedder, UnavailableTextEmbedder>();
        services.TryAddSingleton(new MemoryRecallOptions());
        services.AddScoped<MemoryRecall>();
        services.AddScoped<IMemoryService, MemoryService>();

        return services;
    }
}
