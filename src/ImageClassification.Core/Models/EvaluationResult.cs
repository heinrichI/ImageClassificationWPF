namespace ImageClassification.Core.Models;

public class EvaluationResult
{
    public float Accuracy { get; set; }
    public float Loss { get; set; }
    public int TotalImages { get; set; }
    public int CorrectPredictions { get; set; }
    public Dictionary<string, ClassMetrics> PerClassMetrics { get; set; } = new();
    public float[,]? ConfusionMatrix { get; set; }
    public string[]? ClassLabels { get; set; }
}

public class ClassMetrics
{
    public string ClassName { get; set; } = string.Empty;
    public float Precision { get; set; }
    public float Recall { get; set; }
    public float F1Score { get; set; }
    public int TruePositives { get; set; }
    public int FalsePositives { get; set; }
    public int FalseNegatives { get; set; }
}
