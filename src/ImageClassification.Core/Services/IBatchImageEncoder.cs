namespace ImageClassification.Core.Services;

/// <summary>
/// Batched CLIP image encoder: preprocesses images in parallel on CPU (decode, resize,
/// normalize) and runs batched ONNX inference (GPU when available, CPU fallback). No
/// caching — the caller is responsible for cache management. All outputs are L2-normalized
/// 512-dim embeddings, in the same order as the input; images that fail to preprocess
/// (corrupt file/bytes) come back as zero-filled embeddings.
/// </summary>
public interface IBatchImageEncoder : IDisposable
{
    /// <summary>
    /// Number of images per ONNX inference batch. Live value from user settings — a
    /// change applies from the next search / next batch. Values &lt; 1 fall back to 32.
    /// </summary>
    int BatchSize { get; }

    /// <summary>
    /// Number of batches to prefetch (CPU preprocessing ahead of GPU inference).
    /// Default 2 — keeps the GPU fed while minimizing memory pressure.
    /// </summary>
    int PrefetchCount { get; set; }

    /// <summary>
    /// Encodes a list of image paths into L2-normalized embeddings using batched inference.
    /// Failed images get a zero-filled embedding.
    /// </summary>
    Task<float[][]> EncodeImagesBatchAsync(
        List<string> imagePaths,
        IProgress<(int Current, int Total)>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Encodes in-memory image bytes into L2-normalized embeddings using batched inference.
    /// Failed images get a zero-filled embedding.
    /// </summary>
    Task<float[][]> EncodeImagesFromMemoryBatchAsync(
        List<(int OriginalIndex, byte[] Data)> items,
        IProgress<(int Current, int Total)>? progress = null,
        CancellationToken ct = default);
}