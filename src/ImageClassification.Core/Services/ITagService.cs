using ImageClassification.Core.Models;

namespace ImageClassification.Core.Services;

public interface ITagService : IDisposable
{
    Task LoadModelAsync(string clipOnnxPath);
    Task<List<ImageTagResult>> GenerateTagsAsync(
        List<string> imagePaths,
        string[] candidateTags,
        int topK = 5,
        IProgress<int>? progress = null);
}
