using ImageClassification.Core.Models;

namespace ImageClassification.Tests.Models;

public class ClassificationResultTests
{
    [Fact]
    public void ClassificationResult_PropertiesAreSetCorrectly()
    {
        var probabilities = new Dictionary<string, float>
        {
            ["cat"] = 0.8f,
            ["dog"] = 0.15f,
            ["bird"] = 0.05f
        };

        var result = new ClassificationResult("/path/image.jpg", "cat", 0.8f, probabilities);

        Assert.Equal("/path/image.jpg", result.FilePath);
        Assert.Equal("cat", result.PredictedClass);
        Assert.Equal(0.8f, result.Confidence);
        Assert.Equal(3, result.AllProbabilities.Count);
        Assert.Equal(0.8f, result.AllProbabilities["cat"]);
    }

    [Fact]
    public void ClassificationResult_EmptyProbabilities()
    {
        var result = new ClassificationResult("test.jpg", "unknown", 0f, new Dictionary<string, float>());

        Assert.Equal("test.jpg", result.FilePath);
        Assert.Equal("unknown", result.PredictedClass);
        Assert.Empty(result.AllProbabilities);
    }
}
