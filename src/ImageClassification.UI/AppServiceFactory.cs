using ImageClassification.Core.Services;
using ImageClassification.UI.Configuration;
using ImageClassification.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
 
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
 
                // Bind ThumbnailSettings from configuration
                services.Configure<ThumbnailSettings>(context.Configuration.GetSection("ThumbnailSettings"));
                services.AddSingleton(sp => sp.GetRequiredService<IOptions<ThumbnailSettings>>().Value);

                // Bind BatchImageEncoder settings from configuration
                services.Configure<BatchImageEncoderSettings>(context.Configuration.GetSection("BatchImageEncoder"));
                services.AddSingleton(sp => sp.GetRequiredService<IOptions<BatchImageEncoderSettings>>().Value);
 
                // Thumbnail infrastructure (background loader + LRU cache)
                services.AddSingleton<Services.ThumbnailCache>();
                services.AddSingleton<Services.ThumbnailProvider>();
 
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
