using ImageClassification.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImageClassification.UI;

public static class AppServiceFactory
{
    private static IHost? _host;

    public static IHost Host => _host ??= CreateHostBuilder().Build();

    public static IServiceProvider Services => Host.Services;

    private static IHostBuilder CreateHostBuilder() =>
        Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                // Library-level extensions encapsulate all concrete registrations
                services.AddArchiveReader();
                services.AddVectorStore();
                services.AddImageClassificationCore();

                // Windows
                services.AddTransient<MainWindow>();

                // ViewModels — Singleton so tabs share state
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<TrainViewModel>();
                services.AddSingleton<EvaluateViewModel>();
                services.AddSingleton<ClassifyViewModel>();
                services.AddSingleton<AnalyzeViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<ComicCoverSearchViewModel>();
            });
}
