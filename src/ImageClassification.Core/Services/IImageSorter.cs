using ImageClassification.Core.Models;

namespace ImageClassification.Core.Services;

public interface IImageSorter
{
    Task<SortResult> SortImagesAsync(
        string modelPath,
        string labelFilePath,
        string sourceDirectory,
        float confidenceThreshold,
        int imageSize,
        int batchSize,
        bool moveFiles = false,
        float timestampWindowMinutes = 1f,
        bool skipConflicts = true,
        IProgress<SortProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<List<SortWarning>> DetectTimestampConflictsAsync(
        string modelPath,
        string labelFilePath,
        string sourceDirectory,
        int imageSize,
        float timestampWindowMinutes = 1f);
}

public class SortResult
{
    public int TotalImages { get; set; }
    public int SortedImages { get; set; }
    public int SkippedImages { get; set; }
    public int ConflictsSkipped { get; set; }
    public Dictionary<string, int> FilesPerClass { get; set; } = new();
    public List<SortWarning> Warnings { get; set; } = new();
}

public class SortProgress
{
    public int Current { get; set; }
    public int Total { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public string PredictedClass { get; set; } = string.Empty;
    public float Confidence { get; set; }
}
