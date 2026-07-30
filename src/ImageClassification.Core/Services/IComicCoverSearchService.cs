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
}
