namespace ImageClassification.Core.Interfaces;

/// <summary>
/// Stores and retrieves image embedding vectors.
/// <para>
/// Two dedicated tables serve the comic archive search (one row per archive):
/// <c>archive_embeddings</c> holds the packed vector array of ALL pages of an archive —
/// the row's presence (with fresh file metadata) means the archive is fully cached;
/// <c>cover_embeddings</c> holds the single cover vector.
/// The generic <c>embeddings</c> table (keyed by (modelName, filePath)) serves the
/// per-file caches of the other features (classifier, feature extractor, loose-file CLIP).
/// Cache validity is checked via file metadata (last modified time, file size).
/// </para>
/// </summary>
public interface IVectorStore : IDisposable
{
    /// <summary>
    /// File path of the backing store (e.g. the SQLite database file), or null for
    /// in-memory implementations. Logged at search start so cache location is visible.
    /// </summary>
    string? DatabasePath { get; }

    /// <summary>
    /// Completes when the one-time legacy-cache migration has finished (or was skipped).
    /// The migration runs on a background thread so app startup is never blocked by it;
    /// implementations that migrate lazily must ensure the migrated data is queryable
    /// only after this task completes.
    /// </summary>
    Task WhenReady { get; }

    /// <summary>
    /// Saves or updates an embedding vector for a specific file (generic per-file cache).
    /// </summary>
    Task SaveAsync(string modelName, string filePath,
                   DateTime lastModified, long fileSize, float[] embedding);

    /// <summary>
    /// Returns the cached embedding if the file metadata matches, or null on miss / stale.
    /// </summary>
    Task<float[]?> GetAsync(string modelName, string filePath,
                            DateTime lastModified, long fileSize);

    /// <summary>
    /// Batch version of GetAsync — returns embeddings for multiple files in one batched
    /// round-trip. Implementations must scale to very large entry counts (a single search
    /// can probe hundreds of thousands of files in one call).
    /// The returned dictionary contains a null entry for any file that was not found or is stale.
    /// </summary>
    Task<Dictionary<string, float[]?>> GetBatchAsync(string modelName,
        List<(string FilePath, DateTime LastModified, long FileSize)> entries);

    /// <summary>
    /// Returns the WHOLE packed vector array of every page of the archive in one call
    /// (page 0 first), or null when the archive is not cached or its file metadata
    /// no longer matches. The row records its own page count, so a fresh row also means
    /// "the archive is fully cached" — no separate marker is needed.
    /// </summary>
    Task<(int PageCount, float[][] Pages)?> GetArchiveAsync(string modelName, string archivePath,
        DateTime lastModifiedUtc, long fileSize);

    /// <summary>
    /// Stores (upserts) the packed vector array of all <paramref name="pages" /> of the
    /// archive as a single row. <paramref name="pages" /> must contain exactly
    /// <paramref name="pageCount" /> non-null vectors.
    /// </summary>
    Task SaveArchiveAsync(string modelName, string archivePath, int pageCount,
        DateTime lastModifiedUtc, long fileSize, float[][] pages);

    /// <summary>
    /// Returns the cached cover vector if the file metadata matches, or null on miss / stale.
    /// </summary>
    Task<float[]?> GetCoverAsync(string modelName, string archivePath,
        DateTime lastModifiedUtc, long fileSize);

    /// <summary>
    /// Stores (upserts) the cover vector of the archive.
    /// </summary>
    Task SaveCoverAsync(string modelName, string archivePath,
        DateTime lastModifiedUtc, long fileSize, float[] embedding);

    /// <summary>
    /// Clears all cached embeddings, optionally scoped to a single model.
    /// </summary>
    Task ClearAsync(string? modelName = null);

    /// <summary>
    /// Returns a snapshot of the cache state (file sizes, entry counts by mode, entry age, models).
    /// </summary>
    Task<Core.Models.VectorCacheInfo> GetCacheInfoAsync();

    /// <summary>
    /// Cheap maintenance: checkpoints the WAL into the main database file (TRUNCATE) and runs
    /// PRAGMA optimize. Data is never lost; the store stays usable. Returns immediately on in-memory stores.
    /// </summary>
    Task<Core.Models.VectorCacheMaintenanceResult> OptimizeAsync();

    /// <summary>
    /// Expensive maintenance: fully rebuilds the database file, removing free-space "holes" left
    /// by DELETE / INSERT OR REPLACE. The database is exclusively locked while it runs.
    /// </summary>
    Task<Core.Models.VectorCacheMaintenanceResult> VacuumAsync();

    /// <summary>
    /// Removes cache entries whose source file no longer exists on disk. Only files on disks
    /// that are currently connected are checked; entries for files on disconnected drives
    /// (removable media, offline shares) are kept, since the files may reappear once the
    /// disk is reconnected. All cached rows of a missing archive (its all-pages row, its
    /// cover row and any legacy per-page rows) are removed together.
    /// </summary>
    Task<Core.Models.VectorCacheMaintenanceResult> PruneMissingFilesAsync(string? modelName = null);

    /// <summary>
    /// Ensures the database and schema are initialized (including the one-time migration
    /// from the legacy single-table layout). Call once at startup.
    /// </summary>
    void Initialize();
}