namespace ImageClassification.Core.Services;

/// <summary>
/// Ensures all model files exist on disk at startup. Does NOT load them into memory.
/// </summary>
internal sealed class AppInitializationService : IAppInitializationService
{
    private readonly IModelDownloader _downloader;

    public AppInitializationService(IModelDownloader downloader)
    {
        _downloader = downloader;
    }

    public async Task InitializeAsync()
    {
        foreach (var model in _downloader.GetAvailableModels())
        {
            if (!model.IsDownloaded)
                await _downloader.DownloadModelAsync(model);
        }

        await _downloader.EnsureLabelsFileAsync();
    }
}
