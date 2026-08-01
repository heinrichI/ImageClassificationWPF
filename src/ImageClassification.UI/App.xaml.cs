using System.IO;
using System.Text.Json;
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
        AddCudaPaths();
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

    private static void AddCudaPaths()
    {
        try
        {
            var configFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            if (!File.Exists(configFile)) return;

            var json = File.ReadAllText(configFile);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var paths = new List<string>();
            if (root.TryGetProperty("cuda_path", out var cuda) && !string.IsNullOrWhiteSpace(cuda.GetString()))
                paths.Add(cuda.GetString()!);
            if (root.TryGetProperty("cudnn_path", out var cudnn) && !string.IsNullOrWhiteSpace(cudnn.GetString()))
                paths.Add(cudnn.GetString()!);

            if (paths.Count > 0)
            {
                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                var newPath = string.Join(";", paths) + ";" + currentPath;
                Environment.SetEnvironmentVariable("PATH", newPath);
            }
        }
        catch { }
    }
}