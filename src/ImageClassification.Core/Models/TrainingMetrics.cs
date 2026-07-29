namespace ImageClassification.Core.Models;

public class TrainingMetrics
{
    public List<EpochMetrics> Epochs { get; set; } = new();
    public float BestAccuracy { get; set; }
    public int BestEpoch { get; set; }
    public bool IsRunning { get; set; }
    public int CurrentEpoch { get; set; }
    public int TotalEpochs { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class EpochMetrics
{
    public int Epoch { get; set; }
    public float TrainLoss { get; set; }
    public float TrainAccuracy { get; set; }
    public float ValidationLoss { get; set; }
    public float ValidationAccuracy { get; set; }
    public float LearningRate { get; set; }
}
