namespace ImageClassification.Core.Services;

/// <summary>
/// Encodes a single image into a CLIP embedding vector (512-dim, L2-normalized).
/// Shared by ComicCoverSearchService and ClipTagService to avoid loading the model twice.
/// </summary>
public interface IClipImageEncoder : IDisposable
{
    /// <summary>
    /// Encodes the image at the given path into a 512-dim L2-normalized CLIP embedding.
    /// </summary>
    Task<float[]> EncodeImageAsync(string imagePath, CancellationToken ct = default);
}
