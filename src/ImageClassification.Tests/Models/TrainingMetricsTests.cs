using ImageClassification.Core.Models;

namespace ImageClassification.Tests.Models;

public class TrainingMetricsTests
{
    [Fact]
    public void TrainingMetrics_DefaultValues()
    {
        var metrics = new TrainingMetrics();

        Assert.Empty(metrics.Epochs);
        Assert.Equal(0f, metrics.BestAccuracy);
        Assert.Equal(0, metrics.BestEpoch);
        Assert.False(metrics.IsRunning);
        Assert.Equal(0, metrics.CurrentEpoch);
        Assert.Equal(0, metrics.TotalEpochs);
        Assert.Equal(string.Empty, metrics.Status);
    }

    [Fact]
    public void EpochMetrics_AllPropertiesSet()
    {
        var epoch = new EpochMetrics
        {
            Epoch = 5,
            TrainLoss = 0.25f,
            TrainAccuracy = 0.85f,
            ValidationLoss = 0.3f,
            ValidationAccuracy = 0.82f,
            LearningRate = 0.001f
        };

        Assert.Equal(5, epoch.Epoch);
        Assert.Equal(0.25f, epoch.TrainLoss);
        Assert.Equal(0.85f, epoch.TrainAccuracy);
        Assert.Equal(0.3f, epoch.ValidationLoss);
        Assert.Equal(0.82f, epoch.ValidationAccuracy);
        Assert.Equal(0.001f, epoch.LearningRate);
    }

    [Fact]
    public void TrainingMetrics_CanTrackBestEpoch()
    {
        var metrics = new TrainingMetrics
        {
            Epochs = new List<EpochMetrics>
            {
                new() { Epoch = 1, ValidationAccuracy = 0.7f },
                new() { Epoch = 2, ValidationAccuracy = 0.8f },
                new() { Epoch = 3, ValidationAccuracy = 0.75f }
            },
            BestAccuracy = 0.8f,
            BestEpoch = 2
        };

        Assert.Equal(0.8f, metrics.BestAccuracy);
        Assert.Equal(2, metrics.BestEpoch);
        Assert.Equal(3, metrics.Epochs.Count);
    }
}
