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
}