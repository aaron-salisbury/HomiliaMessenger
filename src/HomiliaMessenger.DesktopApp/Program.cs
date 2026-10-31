using Avalonia;
using CommunityToolkit.Mvvm.DependencyInjection;
using HomiliaMessenger.Data.Database;
using HomiliaMessenger.Presentation.Desktop;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.Threading.Tasks;

namespace HomiliaMessenger.DesktopApp;

internal class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static async Task Main(string[] args)
    {
        ServiceProvider? serviceProvider = null;

        try
        {
            IServiceCollection services = DependencyInjection.BuildServiceCollection();
            serviceProvider = services.BuildServiceProvider();
            await serviceProvider.InitializeApplicationDataAsync();
            Ioc.Default.ConfigureServices(serviceProvider);

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            if (serviceProvider is not null)
            {
                await serviceProvider.DisposeAsync().ConfigureAwait(false);
            }

            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
