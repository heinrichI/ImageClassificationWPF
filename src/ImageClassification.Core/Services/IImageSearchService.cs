using ImageClassification.Core.Models;

namespace ImageClassification.Core.Services;

/// <summary>
/// Searches plain image files (jpg/jpeg/png/bmp/tiff/webp) in a directory tree and
/// ranks them by CLIP cosine similarity against a text query.
/// Batched pipeline like the comic cover search: the per-file vector cache
/// ("ClipViTB32" rows, shared with Analyze/By Tags, freshness = file mtime + size) is
/// probed in one batched round-trip; cache hits are scored without reading the file,
/// misses are encoded by the shared batch image encoder (GPU batches, CPU fallback) and
/// saved back to the per-file cache — a second search of the same directory is a
/// near-instant cache probe.
/// </summary>
public interface IImageSearchService
{
    /// <summary>
    /// Searches <paramref name="directoryPath"/> (optionally including subfolders, see
    /// <paramref name="options"/>) for image files matching <paramref name="query"/>.
    /// Files that fail to read/decode are skipped and reported as <see cref="ImageSearchStatus.Note"/>.
    /// </summary>
    Task<List<ImageSearchResult>> SearchAsync(
        string directoryPath,
        string query,
        ImageSearchOptions? options = null,
        IProgress<ImageSearchStatus>? status = null,
        CancellationToken ct = default);

    /// <summary>
    /// Image-to-image search: <paramref name="queryImagePath"/> is encoded with the SAME
    /// CLIP image encoder ("ClipViTB32", cached per file) as the searched files, so its
    /// embedding queries the per-file vector cache directly — "copy the file's ViT hash
    /// and search by it". The query file itself is excluded from the results (it would
    /// always score ~100% against itself); a query file outside the searched roots is
    /// simply not excluded. A query file that cannot be found/decoded is reported via
    /// <see cref="ImageSearchStatus.Note"/> and yields an empty result.
    /// </summary>
    Task<List<ImageSearchResult>> SearchByImageAsync(
        string directoryPath,
        string queryImagePath,
        ImageSearchOptions? options = null,
        IProgress<ImageSearchStatus>? status = null,
        CancellationToken ct = default);

    /// <summary>
    /// Search by an embedding hash: <paramref name="queryVector"/> is used directly
    /// as the query — typically the base64 "ViT hash" pasted into the search box
    /// (see <see cref="Helpers.VectorCodec"/>) or copied from a result tile via
    /// <see cref="GetImageHashAsync"/>. Nothing is encoded and nothing is excluded:
    /// the vector is compared against the same per-file CLIP cache as every other
    /// search. A vector whose length does not match the cached embeddings
    /// (a hash copied from a different model) is reported via
    /// <see cref="ImageSearchStatus.Note"/> and yields an empty result.
    /// </summary>
    Task<List<ImageSearchResult>> SearchByVectorAsync(
        string directoryPath,
        float[] queryVector,
        ImageSearchOptions? options = null,
        IProgress<ImageSearchStatus>? status = null,
        CancellationToken ct = default);

    /// <summary>
    /// The "ViT hash" of a single image file in its text (base64) form: the file's
    /// CLIP embedding taken from the vector cache (a plain lookup when the file was
    /// ever searched) or encoded with the shared cached encoder on a cache miss.
    /// Returns null when the file is missing or cannot be decoded.
    /// </summary>
    Task<string?> GetImageHashAsync(string imagePath, CancellationToken ct = default);
}