using ImageClassification.Core.Models;

namespace ImageClassification.Tests.Models;

public class EvaluationResultTests
{
    [Fact]
    public void EvaluationResult_DefaultValuesAreZero()
    {
        var result = new EvaluationResult();

        Assert.Equal(0f, result.Accuracy);
        Assert.Equal(0f, result.Loss);
        Assert.Equal(0, result.TotalImages);
        Assert.Equal(0, result.CorrectPredictions);
        Assert.Empty(result.PerClassMetrics);
        Assert.Null(result.ConfusionMatrix);
        Assert.Null(result.ClassLabels);
    }

    [Fact]
    public void ClassMetrics_AllPropertiesSet()
    {
        var metrics = new ClassMetrics
        {
            ClassName = "cat",
            Precision = 0.9f,
            Recall = 0.85f,
            F1Score = 0.874f,
            TruePositives = 85,
            FalsePositives = 10,
            FalseNegatives = 15
        };

        Assert.Equal("cat", metrics.ClassName);
        Assert.Equal(0.9f, metrics.Precision);
        Assert.Equal(0.85f, metrics.Recall);
        Assert.Equal(85, metrics.TruePositives);
        Assert.Equal(10, metrics.FalsePositives);
        Assert.Equal(15, metrics.FalseNegatives);
    }
}
