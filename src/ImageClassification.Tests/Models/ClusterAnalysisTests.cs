using ImageClassification.Core.Models;

namespace ImageClassification.Tests.Models;

public class ClusterAnalysisTests
{
    [Fact]
    public void ClusterInfo_DefaultValues()
    {
        var cluster = new ClusterInfo();

        Assert.Equal(0, cluster.Id);
        Assert.Equal(string.Empty, cluster.SuggestedName);
        Assert.Equal(0, cluster.ImageCount);
        Assert.Empty(cluster.ImagePaths);
    }

    [Fact]
    public void ClusterInfo_CanSetProperties()
    {
        var cluster = new ClusterInfo
        {
            Id = 2,
            SuggestedName = "Animals",
            ImageCount = 15,
            ImagePaths = new List<string> { "a.jpg", "b.jpg", "c.jpg" }
        };

        Assert.Equal(2, cluster.Id);
        Assert.Equal("Animals", cluster.SuggestedName);
        Assert.Equal(15, cluster.ImageCount);
        Assert.Equal(3, cluster.ImagePaths.Count);
    }

    [Fact]
    public void ClusterAnalysisResult_DefaultValues()
    {
        var result = new ClusterAnalysisResult();

        Assert.Equal(0, result.NumClusters);
        Assert.Equal(string.Empty, result.SourceDirectory);
        Assert.Empty(result.Clusters);
    }
}
