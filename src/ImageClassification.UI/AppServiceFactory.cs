using ImageClassification.Core.Services;
using ImageClassification.UI.Configuration;
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
            .ConfigureServices((context, services) =>
            {
                // Library-level extensions encapsulate all concrete registrations
                services.AddArchiveReader();
                services.AddVectorStore();
                services.AddImageClassificationCore();

                // User settings: user-settings.json in the program directory, live (file watcher).
                // The store bridges to core via IOnnxRuntimeOptions / IGpuPipelineOptions;
                // the thumbnail pipeline and the settings view-model read it directly.
                services.AddSingleton<IUserSettingsStore, UserSettingsStore>();
                services.AddSingleton<IOnnxRuntimeOptions, UserSettingsOnnxBridge>();
                services.AddSingleton<IGpuPipelineOptions, UserSettingsGpuBridge>();

                // Thumbnail infrastructure (background loader + bounded cache)
                services.AddSingleton<Services.ThumbnailProvider>();

                // Copy comic search result images (in-memory extraction, no temp files)
                services.AddSingleton<Services.ImageCopyService>();
 
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
