namespace ImageClassification.Core.Models;

public class TrainingConfig
{
    public ModelArchitecture Architecture { get; set; } = ModelArchitecture.EfficientNetB1;
    public string TrainingDirectory { get; set; } = string.Empty;
    public string SaveModelPath { get; set; } = string.Empty;
    public int ImageSize { get; set; } = 224;
    public int BatchSize { get; set; } = 32;
    public int Epochs { get; set; } = 50;
    public float LearningRate { get; set; } = 1e-3f;
    public float WeightDecay { get; set; } = 0.01f;
    public float ValidationSplit { get; set; } = 0.2f;
    public bool UseProgressiveUnfreezing { get; set; } = true;
    public bool UseImageNet21kWeights { get; set; } = false;
    public OptimizerType Optimizer { get; set; } = OptimizerType.AdamW;
    public LRSchedule LRSchedule { get; set; } = LRSchedule.CosineAnnealing;
    public int WarmupEpochs { get; set; } = 5;
    public AugmentationConfig Augmentation { get; set; } = new();
}

public enum OptimizerType
{
    AdamW,
    SGD
}

public enum LRSchedule
{
    CosineAnnealing,
    StepLR,
    OneCycleLR
}
