using FluentValidation;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Business.Modules.Memories;
using HomiliaMessenger.Business.Modules.Profile;
using HomiliaMessenger.Business.Modules.Selfies;
using HomiliaMessenger.Business.Modules.WebSearch;
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
        services.TryAddSingleton(new GroupCoordinatorOptions());
        services.TryAddSingleton<GroupCoordinator>();

        // Integrations may register an embedding provider and the composition root other recall limits; without them,
        // memories are recalled by keywords within the default limits.
        services.TryAddSingleton<ITextEmbedder, UnavailableTextEmbedder>();
        services.TryAddSingleton(new MemoryRecallOptions());
        services.AddScoped<MemoryRecall>();
        services.AddScoped<IMemoryService, MemoryService>();

        // Integrations registers the local image generator; without it, photos can only be chosen from files.
        services.TryAddSingleton<IImageGenerator, UnavailableImageGenerator>();
        services.AddScoped<IPortraitService, PortraitService>();

        // Selfies need a selfie model with face matching, registered by Integrations; without it, none can be asked for.
        services.TryAddSingleton<ISelfieImageGenerator, UnavailableSelfieImageGenerator>();
        services.AddScoped<ISelfieService, SelfieService>();

        // Integrations registers Tavily; without it, contacts never search. One instance shares searches in progress and
        // limits how many run at once.
        services.TryAddSingleton<IWebSearchProvider, UnavailableWebSearchProvider>();
        services.AddSingleton<WebSearchService>();
        services.AddSingleton<IWebSearchService>(provider => provider.GetRequiredService<WebSearchService>());

        return services;
    }
}
