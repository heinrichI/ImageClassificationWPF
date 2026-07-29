using ImageClassification.Core.Models;

namespace ImageClassification.Core.Services;

/// <summary>
/// Evaluates model accuracy on a test directory with class subfolders.
/// Depends on IImageClassifier (DIP) — does not create classifier internally.
/// </summary>
public class ModelEvaluator : IModelEvaluator
{
    private readonly IImageClassifier _classifier;

    public ModelEvaluator(IImageClassifier classifier)
    {
        _classifier = classifier;
    }

    public async Task<EvaluationResult> EvaluateAsync(
        string modelPath,
        string testDirectory,
        int imageSize,
        int batchSize,
        IProgress<int>? progress = null)
    {
        var classDirs = Directory.GetDirectories(testDirectory);
        var classLabels = classDirs.Select(d => Path.GetFileName(d)).ToArray();
        int numClasses = classLabels.Length;

        var labelPath = Path.ChangeExtension(modelPath, ".labels.txt");
        if (!File.Exists(labelPath))
            labelPath = modelPath + ".labels.txt";

        await _classifier.LoadModelAsync(modelPath, labelPath);

        var allImages = new List<(string Path, string TrueLabel)>();
        foreach (var classDir in classDirs)
        {
            var label = Path.GetFileName(classDir);
            var images = Directory.GetFiles(classDir)
                .Where(f => IsImageFile(f))
                .Select(f => (f, label));
            allImages.AddRange(images);
        }

        int total = allImages.Count;
        int correct = 0;
        var confusion = new float[numClasses, numClasses];
        var classCorrect = new int[numClasses];
        var classTotal = new int[numClasses];

        int completed = 0;
        foreach (var (imagePath, trueLabel) in allImages)
        {
            try
            {
                var result = _classifier.Classify(imagePath);
                int trueIdx = Array.IndexOf(classLabels, trueLabel);
                int predIdx = Array.IndexOf(classLabels, result.PredictedClass);

                if (trueIdx >= 0 && predIdx >= 0)
                {
                    confusion[trueIdx, predIdx]++;
                    classTotal[trueIdx]++;

                    if (trueIdx == predIdx)
                    {
                        correct++;
                        classCorrect[trueIdx]++;
                    }
                }
            }
            catch { /* skip unprocessable images */ }

            completed++;
            progress?.Report(completed);
        }

        var perClassMetrics = new Dictionary<string, ClassMetrics>();
        for (int i = 0; i < numClasses; i++)
        {
            int tp = classCorrect[i];
            int fn = classTotal[i] - tp;

            int fp = 0;
            for (int j = 0; j < numClasses; j++)
            {
                if (j != i) fp += (int)confusion[j, i];
            }

            float precision = (tp + fp) > 0 ? (float)tp / (tp + fp) : 0;
            float recall = classTotal[i] > 0 ? (float)tp / classTotal[i] : 0;
            float f1 = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0;

            perClassMetrics[classLabels[i]] = new ClassMetrics
            {
                ClassName = classLabels[i],
                Precision = precision,
                Recall = recall,
                F1Score = f1,
                TruePositives = tp,
                FalsePositives = fp,
                FalseNegatives = fn
            };
        }

        return new EvaluationResult
        {
            Accuracy = total > 0 ? (float)correct / total : 0,
            Loss = 0, // Would need model logits to compute
            TotalImages = total,
            CorrectPredictions = correct,
            PerClassMetrics = perClassMetrics,
            ConfusionMatrix = confusion,
            ClassLabels = classLabels
        };
    }

    private static bool IsImageFile(string path) =>
        path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase);
}
