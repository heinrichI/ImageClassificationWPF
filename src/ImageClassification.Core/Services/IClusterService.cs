namespace ImageClassification.Core.Services;

public interface IClusterService
{
    ClusterResult Cluster(float[][] features, int numClusters, int maxIterations = 100);
    int FindOptimalK(float[][] features, int maxK = 10);
}

public class ClusterResult
{
    public int[] Labels { get; set; } = Array.Empty<int>();
    public float[][] Centroids { get; set; } = Array.Empty<float[]>();
    public int NumClusters { get; set; }
    public Dictionary<int, List<int>> ImageIndicesPerCluster { get; set; } = new();
}
