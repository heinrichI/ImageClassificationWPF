namespace ImageClassification.Core.Services;

/// <summary>
/// Encodes text into a CLIP embedding vector (512-dim, L2-normalized).
/// Uses the same CLIP ViT-B/32 ONNX model's text input branch with proper BPE tokenization.
/// </summary>
public interface IClipTextEncoder : IDisposable
{
    /// <summary>
    /// Encodes the given text into a 512-dim L2-normalized CLIP embedding.
    /// </summary>
    Task<float[]> EncodeTextAsync(string text, CancellationToken ct = default);
}