using System.Windows;
using ImageClassification.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImageClassification.UI;

public partial class App : Application
{
    private readonly IHost _host;

    public App()
    {
        _host = AppServiceFactory.Host;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();

        // Ensure model files exist on disk (do NOT load into memory)
        var initializer = _host.Services.GetRequiredService<IAppInitializationService>();
        await initializer.InitializeAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _host.Services.GetRequiredService<ViewModels.MainViewModel>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        using (_host)
        {
            await _host.StopAsync();
        }

        base.OnExit(e);
    }
}
