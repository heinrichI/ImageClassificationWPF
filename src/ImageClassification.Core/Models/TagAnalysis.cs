namespace ImageClassification.Core.Models;

public class TagAnalysisResult
{
    public List<ImageTagResult> ImageTags { get; set; } = new();
    public Dictionary<string, int> TagCounts { get; set; } = new();
    public string SourceDirectory { get; set; } = string.Empty;
}

public class ImageTagResult
{
    public string FilePath { get; set; } = string.Empty;
    public List<(string Tag, float Score)> Tags { get; set; } = new();
}
