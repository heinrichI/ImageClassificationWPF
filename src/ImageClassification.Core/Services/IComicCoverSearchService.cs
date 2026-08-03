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
    Task<List<ComicCoverResult>> SearchAsync(
        string directoryPath,
        string query,
        IProgress<(int Current, int Total)>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Scans a directory tree for CBZ/CBR archives, extracts ALL image pages,
    /// computes embeddings for each page, and ranks them by similarity to the given text query.
    /// </summary>
    Task<List<ComicCoverResult>> SearchAllPagesAsync(
        string directoryPath,
        string query,
        IProgress<(int Current, int Total)>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Lazily extracts the cover image from a single archive into memory.
    /// Call after SearchAsync to load a thumbnail for a displayed result.
    /// </summary>
    Task<byte[]?> ExtractCoverAsync(string archivePath);

    /// <summary>
    /// Clears the cached embeddings for comic search.
    /// </summary>
    Task ClearCacheAsync();
}