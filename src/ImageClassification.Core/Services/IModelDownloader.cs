using ImageClassification.Core.Models;

namespace ImageClassification.Core.Services;

public interface IModelDownloader
{
    string ModelsDirectory { get; }
    ModelDownloadInfo? GetModel(string filename);
    string GetModelPath(string filename);
    string GetLabelsPath();
    Task DownloadModelAsync(ModelDownloadInfo model, IProgress<int>? progress = null, CancellationToken cancellationToken = default);
    List<ModelDownloadInfo> GetAvailableModels();
    void RefreshDownloadStatus();
    Task EnsureLabelsFileAsync();
}
