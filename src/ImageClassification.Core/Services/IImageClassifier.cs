using ImageClassification.Core.Models;

namespace ImageClassification.Core.Services;

public interface IImageClassifier : IDisposable
{
    ModelInfo Model { get; }
    Task LoadModelAsync(string modelPath, string labelFilePath);
    ClassificationResult Classify(string imagePath);
    Task<List<ClassificationResult>> ClassifyBatchAsync(List<string> imagePaths, IProgress<int>? progress = null);
}
