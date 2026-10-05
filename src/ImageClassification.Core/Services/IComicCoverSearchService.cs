using ImageClassification.Core.Models;

namespace ImageClassification.Core.Services;

/// <summary>
/// Service for searching comic archives by cover image similarity to a text query.
/// </summary>
public interface IComicCoverSearchService : IDisposable
{
    /// <summary>
    /// Loads the CLIP ONNX model into memory. Called lazily before first search.
    /// </summary>
    Task LoadModelAsync(string clipOnnxPath);

    /// <summary>
    /// Scans a directory tree for CBZ/CBR archives, extracts covers, computes embeddings,
    /// and ranks them by similarity to the given text query.
    /// </summary>
    /// <param name="status">Optional reporter for the unified status channel
    /// (<see cref="ComicSearchStatus"/>): phase markers, preformatted detail lines,
    /// progress-bar values (Current/Total = processed/found archives) and GPU-queue state.
    /// The UI shows <c>Detail</c> verbatim and combines <c>Phase</c> with the progress counter.</param>
    Task<List<ComicCoverResult>> SearchAsync(
        string directoryPath,
        string query,
        IProgress<ComicSearchStatus>? status = null,
        CancellationToken ct = default);

    /// <summary>
    /// Scans a directory tree for CBZ/CBR archives, extracts ALL image pages,
    /// computes embeddings for each page, and ranks them by similarity to the given text query.
    /// </summary>
    /// <param name="status">Optional reporter for the unified status channel
    /// (<see cref="ComicSearchStatus"/>): phase markers, preformatted detail lines,
    /// progress-bar values (Current/Total = processed/found archives) and GPU-queue state.
    /// The UI shows <c>Detail</c> verbatim and combines <c>Phase</c> with the progress counter.</param>
    Task<List<ComicCoverResult>> SearchAllPagesAsync(
        string directoryPath,
        string query,
        IProgress<ComicSearchStatus>? status = null,
        CancellationToken ct = default);

    /// <summary>
    /// Lazily extracts a specific page image from a single archive into memory.
    /// Call after search to load a thumbnail for a displayed result
    /// (page index 0 = cover). Page-specific: requesting page N never yields the cover.
    /// </summary>
    Task<byte[]?> ExtractPageAsync(string archivePath, int pageIndex);

    /// <summary>
    /// Clears the cached embeddings for comic search.
    /// </summary>
    Task ClearCacheAsync();
}