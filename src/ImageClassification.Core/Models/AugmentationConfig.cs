namespace ImageClassification.Core.Models;

public class AugmentationConfig
{
    public bool UseTrivialAugment { get; set; } = true;
    public bool UseMixUp { get; set; } = true;
    public float MixUpAlpha { get; set; } = 0.2f;
    public bool UseCutMix { get; set; } = true;
    public float CutMixAlpha { get; set; } = 1.0f;
    public bool UseRandomErasing { get; set; } = true;
    public float LabelSmoothing { get; set; } = 0.1f;
    public float RandomHorizontalFlip { get; set; } = 0.5f;
    public float RandomRotation { get; set; } = 15f;
    public float RandomZoom { get; set; } = 0.1f;
}
