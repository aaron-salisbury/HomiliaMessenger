using Microsoft.Extensions.DependencyInjection;
using HomiliaMessenger.Presentation.Desktop.Base.Controls.RibbonControls;
using HomiliaMessenger.Presentation.Desktop.Settings;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using System;

namespace HomiliaMessenger.Presentation.Desktop;

public static class DependencyInjection
{
    /// <summary>
    /// Register internal presentation-tier services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="applicationDataDirectory">The directory where desktop settings (such as the theme preference) are stored.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    public static IServiceCollection RegisterInternalPresentationServices(this IServiceCollection services, string applicationDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);

        services.AddSingleton<IDesktopSettings>(_ => new DesktopSettings(applicationDataDirectory));
        services.AddSingleton<ThemeService>();

        services.AddSingleton<IRibbonControlFactory, RibbonControlFactory>();

        // View Models and ribbon content.
        foreach (Type assemblyType in typeof(App).Assembly.GetTypes())
        {
            if (assemblyType.IsClass && !assemblyType.IsAbstract)
            {
                if (typeof(BaseViewModel).IsAssignableFrom(assemblyType))
                {
                    services.AddTransient(assemblyType);
                }
                else if (typeof(BaseRibbonControl).IsAssignableFrom(assemblyType))
                {
                    services.AddTransient(assemblyType);
                }
            }
        }

        return services;
    }
}
