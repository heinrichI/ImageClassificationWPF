namespace ImageClassification.Core.Services;

/// <summary>
/// Pure C# k-means clustering implementation.
/// No external dependencies needed.
/// </summary>
public class ClusterService : IClusterService
{
    private readonly Random _rng = new(42);

    public ClusterResult Cluster(float[][] features, int numClusters, int maxIterations = 100)
    {
        if (features.Length == 0 || numClusters <= 0)
            return new ClusterResult { NumClusters = numClusters };

        int n = features.Length;
        int dim = features[0].Length;
        numClusters = Math.Min(numClusters, n);

        // Initialize centroids using k-means++
        var centroids = InitializeCentroids(features, numClusters, dim);
        var labels = new int[n];

        for (int iter = 0; iter < maxIterations; iter++)
        {
            bool changed = false;

            // Assignment step
            for (int i = 0; i < n; i++)
            {
                int bestCluster = 0;
                float bestDist = float.MaxValue;

                for (int c = 0; c < numClusters; c++)
                {
                    float dist = EuclideanDistance(features[i], centroids[c]);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestCluster = c;
                    }
                }

                if (labels[i] != bestCluster)
                {
                    labels[i] = bestCluster;
                    changed = true;
                }
            }

            if (!changed) break;

            // Update step
            var sums = new float[numClusters][];
            var counts = new int[numClusters];

            for (int c = 0; c < numClusters; c++)
            {
                sums[c] = new float[dim];
            }

            for (int i = 0; i < n; i++)
            {
                int c = labels[i];
                counts[c]++;
                for (int d = 0; d < dim; d++)
                {
                    sums[c][d] += features[i][d];
                }
            }

            for (int c = 0; c < numClusters; c++)
            {
                if (counts[c] > 0)
                {
                    for (int d = 0; d < dim; d++)
                    {
                        centroids[c][d] = sums[c][d] / counts[c];
                    }
                }
            }
        }

        // Build result
        var imageIndicesPerCluster = new Dictionary<int, List<int>>();
        for (int c = 0; c < numClusters; c++)
            imageIndicesPerCluster[c] = new List<int>();

        for (int i = 0; i < n; i++)
            imageIndicesPerCluster[labels[i]].Add(i);

        return new ClusterResult
        {
            Labels = labels,
            Centroids = centroids,
            NumClusters = numClusters,
            ImageIndicesPerCluster = imageIndicesPerCluster
        };
    }

    public int FindOptimalK(float[][] features, int maxK = 10)
    {
        if (features.Length <= 2) return 2;
        maxK = Math.Min(maxK, features.Length - 1);

        float bestScore = float.MinValue;
        int bestK = 2;

        for (int k = 2; k <= maxK; k++)
        {
            var result = Cluster(features, k, 50);
            float silhouette = ComputeSilhouette(features, result.Labels, k);
            if (silhouette > bestScore)
            {
                bestScore = silhouette;
                bestK = k;
            }
        }

        return bestK;
    }

    private float[][] InitializeCentroids(float[][] features, int k, int dim)
    {
        int n = features.Length;
        var centroids = new float[k][];

        // First centroid: random
        centroids[0] = (float[])features[_rng.Next(n)].Clone();

        for (int c = 1; c < k; c++)
        {
            var distances = new float[n];
            float totalDist = 0;

            for (int i = 0; i < n; i++)
            {
                float minDist = float.MaxValue;
                for (int j = 0; j < c; j++)
                {
                    float dist = EuclideanDistance(features[i], centroids[j]);
                    minDist = Math.Min(minDist, dist);
                }
                distances[i] = minDist * minDist;
                totalDist += distances[i];
            }

            // Weighted random selection
            float target = (float)_rng.NextDouble() * totalDist;
            float cumulative = 0;
            int selected = 0;
            for (int i = 0; i < n; i++)
            {
                cumulative += distances[i];
                if (cumulative >= target)
                {
                    selected = i;
                    break;
                }
            }

            centroids[c] = (float[])features[selected].Clone();
        }

        return centroids;
    }

    private float EuclideanDistance(float[] a, float[] b)
    {
        float sum = 0;
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            float diff = a[i] - b[i];
            sum += diff * diff;
        }
        return MathF.Sqrt(sum);
    }

    private float ComputeSilhouette(float[][] features, int[] labels, int k)
    {
        int n = features.Length;
        if (n < 2 || k < 2) return 0;

        float totalSilhouette = 0;
        int validCount = 0;

        for (int i = 0; i < n; i++)
        {
            int clusterI = labels[i];

            // a(i) = mean distance to same cluster
            float aSum = 0;
            int aCount = 0;
            for (int j = 0; j < n; j++)
            {
                if (j != i && labels[j] == clusterI)
                {
                    aSum += EuclideanDistance(features[i], features[j]);
                    aCount++;
                }
            }
            if (aCount == 0) continue;
            float a = aSum / aCount;

            // b(i) = min mean distance to other clusters
            float b = float.MaxValue;
            for (int c = 0; c < k; c++)
            {
                if (c == clusterI) continue;
                float bSum = 0;
                int bCount = 0;
                for (int j = 0; j < n; j++)
                {
                    if (labels[j] == c)
                    {
                        bSum += EuclideanDistance(features[i], features[j]);
                        bCount++;
                    }
                }
                if (bCount > 0)
                {
                    b = Math.Min(b, bSum / bCount);
                }
            }

            if (b == float.MaxValue) continue;

            float silhouette = (b - a) / Math.Max(a, b);
            totalSilhouette += silhouette;
            validCount++;
        }

        return validCount > 0 ? totalSilhouette / validCount : 0;
    }
}
