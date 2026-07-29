using ImageClassification.Core.Services;

namespace ImageClassification.Tests.Services;

public class ClusterServiceTests
{
    private readonly ClusterService _service = new();

    [Fact]
    public void Cluster_TwoDistinctGroups_ReturnsTwoClusters()
    {
        // Arrange: two clearly separated groups
        var features = new float[][]
        {
            new[] { 1f, 1f }, new[] { 1.1f, 1f }, new[] { 1f, 1.1f }, // Group A near (1,1)
            new[] { 10f, 10f }, new[] { 10.1f, 10f }, new[] { 10f, 10.1f } // Group B near (10,10)
        };

        // Act
        var result = _service.Cluster(features, 2);

        // Assert
        Assert.Equal(2, result.NumClusters);
        Assert.Equal(6, result.Labels.Length);

        // Each group should be in its own cluster
        var groupA = result.Labels.Take(3).ToList();
        var groupB = result.Labels.Skip(3).ToList();

        Assert.All(groupA, label => Assert.Equal(groupA[0], label));
        Assert.All(groupB, label => Assert.Equal(groupB[0], label));
        Assert.NotEqual(groupA[0], groupB[0]);
    }

    [Fact]
    public void Cluster_SingleElement_ReturnsOneCluster()
    {
        var features = new float[][] { new[] { 1f, 2f } };

        var result = _service.Cluster(features, 1);

        Assert.Equal(1, result.NumClusters);
        Assert.Equal(0, result.Labels[0]);
    }

    [Fact]
    public void Cluster_EmptyFeatures_ReturnsEmptyResult()
    {
        var features = Array.Empty<float[]>();

        var result = _service.Cluster(features, 3);

        Assert.Empty(result.Labels);
    }

    [Fact]
    public void Cluster_MoreClustersThanElements_ClampsToElementCount()
    {
        var features = new float[][]
        {
            new[] { 1f, 1f },
            new[] { 2f, 2f }
        };

        var result = _service.Cluster(features, 10);

        Assert.True(result.NumClusters <= 2);
    }

    [Fact]
    public void FindOptimalK_TwoDistinctGroups_ReturnsK2()
    {
        var features = new float[][]
        {
            new[] { 1f, 1f }, new[] { 1.1f, 1f }, new[] { 1f, 1.1f },
            new[] { 10f, 10f }, new[] { 10.1f, 10f }, new[] { 10f, 10.1f }
        };

        var k = _service.FindOptimalK(features, maxK: 5);

        Assert.Equal(2, k);
    }

    [Fact]
    public void FindOptimalK_ThreeDistinctGroups_ReturnsK3()
    {
        var features = new float[][]
        {
            new[] { 0f, 0f }, new[] { 0.1f, 0f },
            new[] { 5f, 5f }, new[] { 5.1f, 5f },
            new[] { 10f, 10f }, new[] { 10.1f, 10f }
        };

        var k = _service.FindOptimalK(features, maxK: 5);

        Assert.Equal(3, k);
    }

    [Fact]
    public void Cluster_ThreeGroups_AllAssigned()
    {
        var features = new float[][]
        {
            new[] { 0f, 0f }, new[] { 0.1f, 0f }, new[] { 0f, 0.1f },
            new[] { 5f, 5f }, new[] { 5.1f, 5f }, new[] { 5f, 5.1f },
            new[] { 10f, 10f }, new[] { 10.1f, 10f }, new[] { 10f, 10.1f }
        };

        var result = _service.Cluster(features, 3);

        Assert.Equal(3, result.NumClusters);
        Assert.Equal(9, result.Labels.Length);

        // Each cluster should have images
        Assert.All(result.ImageIndicesPerCluster.Values, indices =>
            Assert.True(indices.Count > 0));
    }
}
