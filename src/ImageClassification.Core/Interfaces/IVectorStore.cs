namespace ImageClassification.Core.Interfaces;

/// <summary>
/// Stores and retrieves image embedding vectors keyed by (modelName, filePath).
/// Cache validity is checked via file metadata (last modified time, file size).
/// </summary>
public interface IVectorStore : IDisposable
{
    /// <summary>
    /// File path of the backing store (e.g. the SQLite database file), or null for
    /// in-memory implementations. Logged at search start so cache location is visible.
    /// </summary>
    string? DatabasePath { get; }

    /// <summary>
    /// Saves or updates an embedding vector for a specific file.
    /// </summary>
    Task SaveAsync(string modelName, string filePath,
                   DateTime lastModified, long fileSize, float[] embedding);

    /// <summary>
    /// Returns the cached embedding if the file metadata matches, or null on miss / stale.
    /// </summary>
    Task<float[]?> GetAsync(string modelName, string filePath,
                            DateTime lastModified, long fileSize);

    /// <summary>
    /// Batch version of GetAsync — returns embeddings for multiple files in one round-trip.
    /// The returned dictionary contains a null entry for any file that was not found or is stale.
    /// </summary>
    Task<Dictionary<string, float[]?>> GetBatchAsync(string modelName,
        List<(string FilePath, DateTime LastModified, long FileSize)> entries);

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
    /// Ensures the database and schema are initialized. Call once at startup.
    /// </summary>
    void Initialize();
}
