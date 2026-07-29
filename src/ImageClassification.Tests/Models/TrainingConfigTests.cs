using ImageClassification.Core.Models;

namespace ImageClassification.Tests.Models;

public class TrainingConfigTests
{
    [Fact]
    public void TrainingConfig_DefaultValues()
    {
        var config = new TrainingConfig();

        Assert.Equal(ModelArchitecture.EfficientNetB1, config.Architecture);
        Assert.Equal(224, config.ImageSize);
        Assert.Equal(32, config.BatchSize);
        Assert.Equal(50, config.Epochs);
        Assert.Equal(1e-3f, config.LearningRate);
        Assert.Equal(0.01f, config.WeightDecay);
        Assert.Equal(0.2f, config.ValidationSplit);
        Assert.True(config.UseProgressiveUnfreezing);
        Assert.Equal(OptimizerType.AdamW, config.Optimizer);
        Assert.Equal(LRSchedule.CosineAnnealing, config.LRSchedule);
        Assert.Equal(5, config.WarmupEpochs);
    }

    [Fact]
    public void AugmentationConfig_DefaultValues()
    {
        var aug = new AugmentationConfig();

        Assert.True(aug.UseTrivialAugment);
        Assert.True(aug.UseMixUp);
        Assert.Equal(0.2f, aug.MixUpAlpha);
        Assert.True(aug.UseCutMix);
        Assert.Equal(1.0f, aug.CutMixAlpha);
        Assert.True(aug.UseRandomErasing);
        Assert.Equal(0.1f, aug.LabelSmoothing);
    }

    [Fact]
    public void TrainingConfig_CanSetAllProperties()
    {
        var config = new TrainingConfig
        {
            Architecture = ModelArchitecture.ConvNeXtTiny,
            TrainingDirectory = @"C:\train",
            SaveModelPath = @"C:\model.onnx",
            ImageSize = 224,
            BatchSize = 64,
            Epochs = 100,
            LearningRate = 0.001f,
            WeightDecay = 0.05f,
            ValidationSplit = 0.3f,
            Optimizer = OptimizerType.SGD,
            LRSchedule = LRSchedule.OneCycleLR,
            WarmupEpochs = 10
        };

        Assert.Equal(ModelArchitecture.ConvNeXtTiny, config.Architecture);
        Assert.Equal(@"C:\train", config.TrainingDirectory);
        Assert.Equal(64, config.BatchSize);
        Assert.Equal(100, config.Epochs);
        Assert.Equal(OptimizerType.SGD, config.Optimizer);
        Assert.Equal(LRSchedule.OneCycleLR, config.LRSchedule);
        Assert.Equal(10, config.WarmupEpochs);
    }
}
