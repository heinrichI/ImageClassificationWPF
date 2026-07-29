using ImageClassification.Core.Models;

namespace ImageClassification.Core.Services;

public interface IImageFeatureExtractor : IDisposable
{
    Task LoadModelAsync(string modelPath, int imageSize);
    float[] ExtractFeatures(string imagePath);
    Task<List<ImageFeatures>> ExtractBatchAsync(List<string> imagePaths, IProgress<int>? progress = null);
}

public class ImageFeatures
{
    public string FilePath { get; set; } = string.Empty;
    public float[] Features { get; set; } = Array.Empty<float>();
}
