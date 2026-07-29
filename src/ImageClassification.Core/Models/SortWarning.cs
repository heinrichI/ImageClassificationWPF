namespace ImageClassification.Core.Models;

public class SortWarning
{
    public string Message { get; set; } = string.Empty;
    public List<string> FilePaths { get; set; } = new();
    public List<string> PredictedClasses { get; set; } = new();
    public DateTime TimeWindowStart { get; set; }
    public DateTime TimeWindowEnd { get; set; }

    public string Summary =>
        $"Files created within {(TimeWindowEnd - TimeWindowStart).TotalSeconds:F0}s " +
        $"({TimeWindowStart:HH:mm:ss} - {TimeWindowEnd:HH:mm:ss}) " +
        $"predicted as different classes: [{string.Join(", ", PredictedClasses.Distinct())}]";
}
