using FluentValidation;
using HomiliaMessenger.Business.Modules.Contacts;
using Microsoft.Extensions.DependencyInjection;
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

        return services;
    }
}
