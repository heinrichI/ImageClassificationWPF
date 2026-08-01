using ImageClassification.Core.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering ImageClassification.Core services in the DI container.
/// </summary>
public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Core services (classifiers, trainers, CLIP encoders, etc.)
    /// with their interfaces. Implementations are internal — callers resolve only interfaces.
    /// </summary>
    public static IServiceCollection AddImageClassificationCore(this IServiceCollection services)
    {
        // ML services
        services.AddSingleton<OnnxClassifier>();
        services.AddSingleton<IImageClassifier, CachedImageClassifier>();
        services.AddSingleton<IModelTrainer, TorchSharpTrainer>();
        services.AddSingleton<IModelEvaluator, ModelEvaluator>();
        services.AddSingleton<IImageSorter, ImageSorter>();

        // Concrete registrations for decorator injection
        services.AddSingleton<ImageFeatureExtractor>();
        services.AddSingleton<ClipImageEncoder>();
        services.AddSingleton<ClipTextEncoder>();
        services.AddSingleton<BatchImageEncoder>();

        // Public-facing interface → cached decorator
        services.AddSingleton<IClipImageEncoder, CachedClipImageEncoder>();
        services.AddSingleton<IClipTextEncoder>(sp => sp.GetRequiredService<ClipTextEncoder>());
        services.AddSingleton<IImageFeatureExtractor, CachedImageFeatureExtractor>();
        services.AddSingleton<IClusterService, ClusterService>();
        services.AddSingleton<ITagService, ClipTagService>();
        // Use factory to call parameterless constructor — DI would otherwise
        // resolve IEnumerable<ModelDownloadInfo?> as empty and skip default models
        services.AddSingleton<IModelDownloader>(_ => new ModelDownloader());
        services.AddSingleton<IAppInitializationService, AppInitializationService>();
        services.AddSingleton<IComicCoverSearchService, ComicCoverSearchService>();

        return services;
    }
}
