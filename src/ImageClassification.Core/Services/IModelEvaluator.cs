using ImageClassification.Core.Models;

namespace ImageClassification.Core.Services;

public interface IModelEvaluator
{
    Task<EvaluationResult> EvaluateAsync(string testDirectory, int imageSize, int batchSize, IProgress<int>? progress = null);
}
