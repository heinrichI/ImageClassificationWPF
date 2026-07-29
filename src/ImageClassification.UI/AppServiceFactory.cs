using ImageClassification.Core.Services;
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
                // Core services — registered by interface (DIP)
                services.AddSingleton<IImageClassifier, OnnxClassifier>();
                services.AddSingleton<IModelTrainer, TorchSharpTrainer>();
                services.AddSingleton<IModelEvaluator, ModelEvaluator>();
                services.AddSingleton<IImageSorter, ImageSorter>();
                services.AddSingleton<IImageFeatureExtractor, ImageFeatureExtractor>();
                services.AddSingleton<IClusterService, ClusterService>();
                services.AddSingleton<ITagService, ClipTagService>();
                services.AddSingleton<IModelDownloader, ModelDownloader>();

                // Windows
                services.AddTransient<MainWindow>();

                // ViewModels
                services.AddSingleton<MainViewModel>();
                services.AddTransient<TrainViewModel>();
                services.AddTransient<EvaluateViewModel>();
                services.AddTransient<ClassifyViewModel>();
                services.AddTransient<AnalyzeViewModel>();
                services.AddTransient<SettingsViewModel>();
            });
}
