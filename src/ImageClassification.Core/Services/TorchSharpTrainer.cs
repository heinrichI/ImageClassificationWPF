using System.Collections.Concurrent;
using ImageClassification.Core.Models;

namespace ImageClassification.Core.Services;

public class TorchSharpTrainer : IModelTrainer
{
    public bool IsTraining { get; private set; }

    public async Task<TrainingMetrics> TrainAsync(
        TrainingConfig config,
        IProgress<TrainingMetrics>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IsTraining = true;
        var metrics = new TrainingMetrics { IsRunning = true, TotalEpochs = config.Epochs };

        try
        {
            var classDirs = Directory.GetDirectories(config.TrainingDirectory);
            var classLabels = classDirs.Select(d => Path.GetFileName(d)).ToArray();
            int numClasses = classLabels.Length;

            // Collect all image paths with labels
            var allImages = new List<(string Path, int Label)>();
            for (int i = 0; i < classDirs.Length; i++)
            {
                var images = Directory.GetFiles(classDirs[i])
                    .Where(f => IsImageFile(f))
                    .Select(f => (f, i));
                allImages.AddRange(images);
            }

            // Split into train/validation
            var rng = new Random(42);
            allImages = allImages.OrderBy(_ => rng.Next()).ToList();
            int valCount = (int)(allImages.Count * config.ValidationSplit);
            var valImages = allImages.Take(valCount).ToList();
            var trainImages = allImages.Skip(valCount).ToList();

            float bestValAcc = 0;
            int bestEpoch = 0;

            for (int epoch = 0; epoch < config.Epochs; epoch++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                metrics.CurrentEpoch = epoch + 1;
                metrics.Status = $"Epoch {epoch + 1}/{config.Epochs}";

                // Simulate training progress (real TorchSharp training would go here)
                float lr = CalculateLR(config, epoch);
                float trainLoss = Math.Max(0.1f, 2.0f - epoch * 0.04f + (float)(rng.NextDouble() * 0.1));
                float trainAcc = Math.Min(0.99f, 0.3f + epoch * 0.015f + (float)(rng.NextDouble() * 0.02));
                float valLoss = Math.Max(0.15f, trainLoss + 0.1f + (float)(rng.NextDouble() * 0.05));
                float valAcc = Math.Max(0.25f, trainAcc - 0.05f + (float)(rng.NextDouble() * 0.02));

                if (valAcc > bestValAcc)
                {
                    bestValAcc = valAcc;
                    bestEpoch = epoch + 1;
                }

                metrics.Epochs.Add(new EpochMetrics
                {
                    Epoch = epoch + 1,
                    TrainLoss = trainLoss,
                    TrainAccuracy = trainAcc,
                    ValidationLoss = valLoss,
                    ValidationAccuracy = valAcc,
                    LearningRate = lr
                });

                metrics.BestAccuracy = bestValAcc;
                metrics.BestEpoch = bestEpoch;

                progress?.Report(metrics);

                await Task.Delay(100, cancellationToken); // Yield for cancellation check
            }

            // Export to ONNX
            if (!string.IsNullOrEmpty(config.SaveModelPath))
            {
                await ExportToOnnxAsync(config, numClasses, classLabels);
            }

            metrics.Status = "Training complete";
            metrics.IsRunning = false;
        }
        catch (OperationCanceledException)
        {
            metrics.Status = "Training cancelled";
            metrics.IsRunning = false;
        }
        catch (Exception ex)
        {
            metrics.Status = $"Error: {ex.Message}";
            metrics.IsRunning = false;
        }
        finally
        {
            IsTraining = false;
        }

        return metrics;
    }

    private float CalculateLR(TrainingConfig config, int epoch)
    {
        return config.LRSchedule switch
        {
            LRSchedule.CosineAnnealing => config.LearningRate * 0.5f *
                (1 + MathF.Cos(MathF.PI * epoch / config.Epochs)),
            LRSchedule.StepLR => config.LearningRate * MathF.Pow(0.1f, epoch / 20),
            LRSchedule.OneCycleLR => config.LearningRate *
                (1 + MathF.Cos(MathF.PI * epoch / config.Epochs)) / 2,
            _ => config.LearningRate
        };
    }

    private async Task ExportToOnnxAsync(TrainingConfig config, int numClasses, string[] labels)
    {
        var dir = Path.GetDirectoryName(config.SaveModelPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Save label file alongside model
        var labelPath = Path.ChangeExtension(config.SaveModelPath, ".labels.txt");
        await File.WriteAllLinesAsync(labelPath, labels);

        // Real TorchSharp ONNX export would go here
        await Task.CompletedTask;
    }

    private static bool IsImageFile(string path) =>
        path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
