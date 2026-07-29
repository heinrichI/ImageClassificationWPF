namespace ImageClassification.Core.Models;

public class ClusterAnalysisResult
{
    public int NumClusters { get; set; }
    public string SourceDirectory { get; set; } = string.Empty;
    public List<ClusterInfo> Clusters { get; set; } = new();
}

public class ClusterInfo
{
    public int Id { get; set; }
    public string SuggestedName { get; set; } = string.Empty;
    public int ImageCount { get; set; }
    public List<string> ImagePaths { get; set; } = new();
}
