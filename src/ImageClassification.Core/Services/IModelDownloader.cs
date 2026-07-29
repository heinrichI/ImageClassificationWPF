using ImageClassification.Core.Models;

namespace ImageClassification.Core.Services;

public interface IModelDownloader
{
    string ModelsDirectory { get; }
    Task DownloadModelAsync(ModelDownloadInfo model, IProgress<int>? progress = null, CancellationToken cancellationToken = default);
    List<ModelDownloadInfo> GetAvailableModels();
    void RefreshDownloadStatus();
}
