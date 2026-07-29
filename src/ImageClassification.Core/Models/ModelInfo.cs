namespace ImageClassification.Core.Models;

public class ModelInfo
{
    public string ModelPath { get; set; } = string.Empty;
    public ModelArchitecture Architecture { get; set; }
    public int ImageSize { get; set; }
    public int NumClasses { get; set; }
    public string[] ClassLabels { get; set; } = Array.Empty<string>();
    public long FileSizeBytes { get; set; }
    public DateTime TrainedDate { get; set; }
    public Dictionary<string, float> TrainingMetrics { get; set; } = new();
}
