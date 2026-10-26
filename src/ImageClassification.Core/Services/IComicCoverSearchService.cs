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
    /// Image-to-cover search: <paramref name="queryImagePath"/> is encoded with the SAME
    /// CLIP image encoder ("ClipViTB32", cached per file) as the archive pages, so its
    /// embedding queries the per-file vector cache directly. Archives whose path equals
    /// the query path are excluded from the results (a query outside the searched roots
    /// is simply not excluded). A query file that cannot be found/decoded is reported
    /// via <see cref="ComicSearchStatus.Note"/> and yields an empty result.
    /// </summary>
    Task<List<ComicCoverResult>> SearchByImageAsync(
        string directoryPath,
        string queryImagePath,
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
    /// Image-to-page search over ALL pages: <paramref name="queryImagePath"/> is encoded
    /// with the same cached CLIP image encoder as the archive pages (see
    /// <see cref="SearchByImageAsync(string, string, IProgress{ComicSearchStatus}?, CancellationToken)"/>
    /// for the query semantics).
    /// </summary>
    Task<List<ComicCoverResult>> SearchAllPagesByImageAsync(
        string directoryPath,
        string queryImagePath,
        IProgress<ComicSearchStatus>? status = null,
        CancellationToken ct = default);

    /// <summary>
    /// Search by a page of a KNOWN archive — the "copy the tile's ViT hash" entry point:
    /// the page vector of <paramref name="queryArchivePath"/> / <paramref name="queryPageIndex"/>
    /// is taken from the vector store when fresh (no archive open, no inference) and only
    /// encoded on a cache miss. The query archive itself is excluded from the results
    /// (its own cover/pages would score ~100%). A query that cannot be resolved is
    /// reported via <see cref="ComicSearchStatus.Note"/> and yields an empty result.
    /// </summary>
    Task<List<ComicCoverResult>> SearchByArchivePageAsync(
        string directoryPath,
        string queryArchivePath,
        int queryPageIndex,
        IProgress<ComicSearchStatus>? status = null,
        CancellationToken ct = default);

    /// <summary>
    /// All-pages variant of <see cref="SearchByArchivePageAsync"/>: every page of every
    /// archive is ranked by similarity to the known archive's page (query archive excluded).
    /// </summary>
    Task<List<ComicCoverResult>> SearchAllPagesByArchivePageAsync(
        string directoryPath,
        string queryArchivePath,
        int queryPageIndex,
        IProgress<ComicSearchStatus>? status = null,
        CancellationToken ct = default);

    /// <summary>
    /// Search by an embedding hash: <paramref name="queryVector"/> is used directly
    /// as the query — typically the base64 "ViT hash" pasted into the search box
    /// (see <see cref="Helpers.VectorCodec"/>) or copied from a result tile via
    /// <see cref="GetPageHashAsync"/>. Nothing is encoded and nothing is excluded:
    /// the vector is compared against the same CLIP page cache as every other
    /// search. A vector whose length does not match the cached embeddings
    /// (a hash copied from a different model) is reported via
    /// <see cref="ComicSearchStatus.Note"/> and yields an empty result.
    /// </summary>
    Task<List<ComicCoverResult>> SearchByVectorAsync(
        string directoryPath,
        float[] queryVector,
        IProgress<ComicSearchStatus>? status = null,
        CancellationToken ct = default);

    /// <summary>All-pages variant of <see cref="SearchByVectorAsync"/>.</summary>
    Task<List<ComicCoverResult>> SearchAllPagesByVectorAsync(
        string directoryPath,
        float[] queryVector,
        IProgress<ComicSearchStatus>? status = null,
        CancellationToken ct = default);

    /// <summary>
    /// The "ViT hash" of one archive page in its text (base64) form: the page's CLIP
    /// embedding taken from the vector store when fresh (a single SQL lookup — no
    /// archive open, no inference) or extracted + encoded on a cache miss. Returns
    /// null when the archive/page is missing or cannot be decoded.
    /// </summary>
    Task<string?> GetPageHashAsync(string archivePath, int pageIndex, CancellationToken ct = default);

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