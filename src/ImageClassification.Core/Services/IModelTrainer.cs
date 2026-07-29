using ImageClassification.Core.Models;

namespace ImageClassification.Core.Services;

public interface IModelTrainer : IDisposable
{
    Task<TrainingMetrics> TrainAsync(TrainingConfig config, IProgress<TrainingMetrics>? progress = null, CancellationToken cancellationToken = default);
    bool IsTraining { get; }
}
