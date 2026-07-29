using ImageClassification.Core.Models;

namespace ImageClassification.Core.Services;

/// <summary>
/// Sorts images into class subdirectories based on model predictions.
/// Includes timestamp conflict detection to prevent sorting images
/// that were created within the same time window into different classes.
/// </summary>
public class ImageSorter : IImageSorter
{
    private readonly IImageClassifier _classifier;

    public ImageSorter(IImageClassifier classifier)
    {
        _classifier = classifier;
    }

    public async Task<List<SortWarning>> DetectTimestampConflictsAsync(
        string modelPath,
        string labelFilePath,
        string sourceDirectory,
        int imageSize,
        float timestampWindowMinutes = 1f)
    {
        await _classifier.LoadModelAsync(modelPath, labelFilePath);

        var imageFiles = GetImageFiles(sourceDirectory);
        if (imageFiles.Count == 0) return new List<SortWarning>();

        // Classify all images
        var classifications = new List<(string Path, string Class, DateTime Created)>();
        foreach (var path in imageFiles)
        {
            var result = _classifier.Classify(path);
            var created = File.GetCreationTime(path);
            classifications.Add((path, result.PredictedClass, created));
        }

        return FindTimestampConflicts(classifications, timestampWindowMinutes);
    }

    public async Task<SortResult> SortImagesAsync(
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
        CancellationToken cancellationToken = default)
    {
        await _classifier.LoadModelAsync(modelPath, labelFilePath);

        var imageFiles = GetImageFiles(sourceDirectory);
        var result = new SortResult { TotalImages = imageFiles.Count };

        if (imageFiles.Count == 0) return result;

        // Classify all images with timestamps
        var classifications = new List<(string Path, string Class, float Confidence, DateTime Created)>();
        foreach (var filePath in imageFiles)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                var classification = _classifier.Classify(filePath);
                var created = File.GetCreationTime(filePath);
                classifications.Add((filePath, classification.PredictedClass, classification.Confidence, created));
            }
            catch
            {
                result.SkippedImages++;
            }
        }

        // Detect timestamp conflicts
        var conflictPaths = new HashSet<string>();
        if (timestampWindowMinutes > 0)
        {
            var simpleClassifications = classifications.Select(c => (c.Path, c.Class, c.Created)).ToList();
            var warnings = FindTimestampConflicts(simpleClassifications, timestampWindowMinutes);
            result.Warnings = warnings;

            if (skipConflicts)
            {
                foreach (var warning in warnings)
                {
                    foreach (var path in warning.FilePaths)
                    {
                        conflictPaths.Add(path);
                    }
                }
            }
        }

        // Sort images
        int completed = 0;
        foreach (var (filePath, predictedClass, confidence, created) in classifications)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Skip conflicting files if requested
            if (conflictPaths.Contains(filePath))
            {
                result.ConflictsSkipped++;
                result.SkippedImages++;
                completed++;
                continue;
            }

            try
            {
                if (confidence >= confidenceThreshold)
                {
                    var targetDir = Path.Combine(sourceDirectory, predictedClass);
                    Directory.CreateDirectory(targetDir);

                    var targetPath = Path.Combine(targetDir, Path.GetFileName(filePath));

                    if (moveFiles)
                        File.Move(filePath, targetPath);
                    else
                        File.Copy(filePath, targetPath, overwrite: true);

                    result.SortedImages++;

                    if (!result.FilesPerClass.ContainsKey(predictedClass))
                        result.FilesPerClass[predictedClass] = 0;
                    result.FilesPerClass[predictedClass]++;
                }
                else
                {
                    result.SkippedImages++;
                }
            }
            catch
            {
                result.SkippedImages++;
            }

            completed++;
            progress?.Report(new SortProgress
            {
                Current = completed,
                Total = imageFiles.Count,
                CurrentFile = Path.GetFileName(filePath),
                PredictedClass = predictedClass,
                Confidence = confidence
            });
        }

        return result;
    }

    /// <summary>
    /// Finds groups of images created within the same time window
    /// that are predicted into different classes.
    /// </summary>
    public static List<SortWarning> FindTimestampConflicts(
        List<(string Path, string Class, DateTime Created)> classifications,
        float windowMinutes)
    {
        var warnings = new List<SortWarning>();
        if (classifications.Count < 2 || windowMinutes <= 0) return warnings;

        // Sort by creation time
        var sorted = classifications.OrderBy(c => c.Created).ToList();

        // Group into time windows using sliding window
        var window = TimeSpan.FromMinutes(windowMinutes);
        var used = new HashSet<int>();

        for (int i = 0; i < sorted.Count; i++)
        {
            if (used.Contains(i)) continue;

            var group = new List<(string Path, string Class, DateTime Created)> { sorted[i] };
            var groupClasses = new HashSet<string> { sorted[i].Class };

            for (int j = i + 1; j < sorted.Count; j++)
            {
                if (used.Contains(j)) continue;
                if (sorted[j].Created - sorted[i].Created >= window) break;

                group.Add(sorted[j]);
                groupClasses.Add(sorted[j].Class);
            }

            // Conflict: same time window, different classes
            if (groupClasses.Count > 1 && group.Count > 1)
            {
                warnings.Add(new SortWarning
                {
                    Message = $"Timestamp conflict: {group.Count} images within {windowMinutes}min window predicted as {groupClasses.Count} different classes",
                    FilePaths = group.Select(g => g.Path).ToList(),
                    PredictedClasses = group.Select(g => g.Class).ToList(),
                    TimeWindowStart = group.First().Created,
                    TimeWindowEnd = group.Last().Created
                });

                foreach (var item in group)
                {
                    used.Add(sorted.IndexOf(item));
                }
            }
        }

        return warnings;
    }

    private static List<string> GetImageFiles(string directory)
    {
        return Directory.GetFiles(directory)
            .Where(f => IsImageFile(f))
            .ToList();
    }

    private static bool IsImageFile(string path) =>
        path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase);
}
