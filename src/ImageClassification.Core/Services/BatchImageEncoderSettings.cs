namespace ImageClassification.Core.Services;

/// <summary>
/// Configuration for <see cref="BatchImageEncoder"/>.
/// Bound from configuration section: "BatchImageEncoder".
/// </summary>
public sealed class BatchImageEncoderSettings
{
    /// <summary>
    /// Number of images per ONNX inference batch.
    /// </summary>
    public int BatchSize { get; set; } = 32;
}