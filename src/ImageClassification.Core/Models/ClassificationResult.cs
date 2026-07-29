namespace ImageClassification.Core.Models;

public record ClassificationResult(
    string FilePath,
    string PredictedClass,
    float Confidence,
    Dictionary<string, float> AllProbabilities);
