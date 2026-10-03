using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;

namespace ImageClassification.VectorStore;

/// <summary>
/// SQLite-based vector embedding cache.
/// Stores float[] as BLOB: 4-byte LE int32 (dim) + dim*4 bytes of float data.
/// Uses SqliteConnectionPool so that concurrent reads do not block each other.
/// </summary>
internal sealed class SqliteVectorStore : IVectorStore
{
    private readonly SqliteConnectionPool _pool;
    private readonly string? _dbPath;
    private readonly ILogger<SqliteVectorStore>? _logger;
    private bool _disposed;

    public string? DatabasePath => _dbPath;

    public SqliteVectorStore(string? dbPath = null, ILogger<SqliteVectorStore>? logger = null)
    {
        var path = dbPath ?? Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "vector_cache.db");
        _dbPath = path;
        _pool = new SqliteConnectionPool(path);
        _logger = logger;
        Initialize();
    }

    /// <summary>
    /// Internal constructor for testing — allows injecting a pre-built pool.
    /// </summary>
    internal SqliteVectorStore(SqliteConnectionPool pool, string? dbPath = null, ILogger<SqliteVectorStore>? logger = null)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _dbPath = dbPath;
        _logger = logger;
        Initialize();
    }

    public void Initialize()
    {
        var conn = _pool.Rent();
        try
        {
            using var cmd = new SqliteCommand(@"
                CREATE TABLE IF NOT EXISTS embeddings (
                    model_name    TEXT    NOT NULL,
                    file_path     TEXT    NOT NULL,
                    last_modified TEXT    NOT NULL,
                    file_size     INTEGER NOT NULL,
                    embedding     BLOB    NOT NULL,
                    created_at    TEXT    NOT NULL,
                    PRIMARY KEY (model_name, file_path)
                )");
            cmd.Connection = conn;
            cmd.ExecuteNonQuery();

            // Enable WAL mode for better concurrent reads
            using var wal = new SqliteCommand("PRAGMA journal_mode=WAL;");
            wal.Connection = conn;
            wal.ExecuteNonQuery();
        }
        finally
        {
            _pool.Return(conn);
        }
    }

    public async Task SaveAsync(string modelName, string filePath,
        DateTime lastModified, long fileSize, float[] embedding)
    {
        var conn = _pool.Rent();
        try
        {
            using var cmd = new SqliteCommand(@"
                INSERT OR REPLACE INTO embeddings
                    (model_name, file_path, last_modified, file_size, embedding, created_at)
                VALUES (@mn, @fp, @lm, @fs, @emb, @ca)");

            cmd.Connection = conn;
            cmd.Parameters.AddWithValue("@mn", modelName);
            cmd.Parameters.AddWithValue("@fp", filePath);
            cmd.Parameters.AddWithValue("@lm", lastModified.ToString("O"));
            cmd.Parameters.AddWithValue("@fs", fileSize);
            cmd.Parameters.AddWithValue("@emb", Serialize(embedding));
            cmd.Parameters.AddWithValue("@ca", DateTime.UtcNow.ToString("O"));

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _pool.Return(conn);
        }
    }

    public async Task<float[]?> GetAsync(string modelName, string filePath,
        DateTime lastModified, long fileSize)
    {
        var conn = _pool.Rent();
        try
        {
            using var cmd = new SqliteCommand(@"
                SELECT embedding, last_modified, file_size
                FROM embeddings
                WHERE model_name = @mn AND file_path = @fp");

            cmd.Connection = conn;
            cmd.Parameters.AddWithValue("@mn", modelName);
            cmd.Parameters.AddWithValue("@fp", filePath);

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (await reader.ReadAsync().ConfigureAwait(false))
            {
                var storedLm = reader.GetString(1);
                var storedFs = reader.GetInt64(2);
                var currentLm = lastModified.ToString("O");

                if (storedLm == currentLm && storedFs == fileSize)
                    return Deserialize(reader.GetFieldValue<byte[]>(0));

                _logger?.LogDebug(
                    "Stale: {ModelName}/{FilePath} " +
                    "stored(lm={StoredLm},fs={StoredFs}) != " +
                    "current(lm={CurrentLm},fs={FileSize})",
                    modelName, filePath, storedLm, storedFs, currentLm, fileSize);
            }
            else
            {
                _logger?.LogDebug(
                    "Miss: {ModelName}/{FilePath} not found", modelName, filePath);
            }

            return null;
        }
        finally
        {
            _pool.Return(conn);
        }
    }

    public async Task<Dictionary<string, float[]?>> GetBatchAsync(string modelName,
        List<(string FilePath, DateTime LastModified, long FileSize)> entries)
    {
        var results = new Dictionary<string, float[]?>(entries.Count);
        foreach (var (path, _, _) in entries)
            results[path] = null;

        if (entries.Count == 0)
            return results;

        var conn = _pool.Rent();
        try
        {
            // Build parameterised IN clause
            var paramNames = new List<string>(entries.Count);
            var cmd = new SqliteCommand();
            cmd.Connection = conn;

            for (int i = 0; i < entries.Count; i++)
            {
                var pName = $"@fp{i}";
                paramNames.Add(pName);
                cmd.Parameters.AddWithValue(pName, entries[i].FilePath);
            }

            cmd.CommandText = "SELECT file_path, embedding, last_modified, file_size " +
                "FROM embeddings " +
                $"WHERE model_name = @mn AND file_path IN ({string.Join(",", paramNames)})";

            cmd.Parameters.AddWithValue("@mn", modelName);

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            int hits = 0;
            int stale = 0;
            string? firstStaleDetail = null;
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var filePath = reader.GetString(0);
                var storedLm = reader.GetString(2);
                var storedFs = reader.GetInt64(3);

                var entryIndex = entries.FindIndex(e => e.FilePath == filePath);
                if (entryIndex < 0) continue;

                var (_, currentLm, currentFs) = entries[entryIndex];
                if (storedLm == currentLm.ToString("O") && storedFs == currentFs)
                {
                    results[filePath] = Deserialize(reader.GetFieldValue<byte[]>(1));
                    hits++;
                }
                else
                {
                    stale++;
                    // Remember the first stale entry so the aggregated line shows a concrete sample
                    if (firstStaleDetail is null)
                    {
                        firstStaleDetail = $"; first stale: {filePath} stored(lm={storedLm}, fs={storedFs}) vs current(lm={currentLm.ToString("O")}, fs={currentFs})";
                    }
                }
            }

            // One aggregated line per batch instead of one line per entry —
            // per-entry debug output floods Output/DebugView during large searches
            if (_logger is not null && (stale > 0 || hits < entries.Count))
            {
                _logger.LogDebug(
                    "[VectorStore] Batch: {Requested} requested, {Hits} hit(s), {Stale} stale, {Missing} missing (model {ModelName}); first key: {FirstKey}{Sample}",
                    entries.Count, hits, stale, entries.Count - hits - stale, modelName,
                    entries[0].FilePath,
                    firstStaleDetail ?? string.Empty);
            }
        }
        finally
        {
            _pool.Return(conn);
        }

        return results;
    }

    public async Task ClearAsync(string? modelName = null)
    {
        var conn = _pool.Rent();
        try
        {
            using var cmd = new SqliteCommand(
                modelName is null
                    ? "DELETE FROM embeddings"
                    : "DELETE FROM embeddings WHERE model_name = @mn");

            cmd.Connection = conn;
            if (modelName is not null)
                cmd.Parameters.AddWithValue("@mn", modelName);

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _pool.Return(conn);
        }
    }

    public async Task<VectorCacheInfo> GetCacheInfoAsync()
    {
        long total = 0, allPages = 0;
        DateTime? oldest = null, newest = null;
        var models = new List<string>();

        var conn = _pool.Rent();
        try
        {
            using var cmd = new SqliteCommand(@"
                SELECT model_name,
                       COUNT(*),
                       COALESCE(SUM(CASE WHEN instr(file_path, '|page=') > 0 THEN 1 ELSE 0 END), 0),
                       MIN(created_at),
                       MAX(created_at)
                FROM embeddings
                GROUP BY model_name");
            cmd.Connection = conn;

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                models.Add(reader.GetString(0));
                total += reader.GetInt64(1);
                allPages += reader.GetInt64(2);
                oldest = MinUtc(oldest, reader.GetString(3));
                newest = MaxUtc(newest, reader.GetString(4));
            }
        }
        finally
        {
            _pool.Return(conn);
        }

        return new VectorCacheInfo(
            DatabasePath,
            SafeFileSize(_dbPath),
            SafeFileSize(_dbPath is null ? null : _dbPath + "-wal"),
            total,
            total - allPages,
            allPages,
            oldest,
            newest,
            string.Join(", ", models));
    }

    public async Task<VectorCacheMaintenanceResult> OptimizeAsync()
    {
        if (_dbPath is null)
            return new VectorCacheMaintenanceResult("optimize", true, "In-memory store — nothing to do.");

        try
        {
            var conn = _pool.Rent();
            try
            {
                using var cmd = new SqliteCommand("PRAGMA wal_checkpoint(TRUNCATE)");
                cmd.Connection = conn;

                int busy = -1, log = -1, checkpointed = -1;
                using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        busy = reader.GetInt32(0);
                        log = reader.GetInt32(1);
                        checkpointed = reader.GetInt32(2);
                    }
                }

                using (var opt = new SqliteCommand("PRAGMA optimize"))
                {
                    opt.Connection = conn;
                    await opt.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                var dbSize = SafeFileSize(_dbPath);
                var walSize = SafeFileSize(_dbPath + "-wal");
                var detail = $"WAL: {log} frame(s), {checkpointed} checkpointed, file truncated to {walSize:N0} bytes; " +
                             $"database file {dbSize:N0} bytes; busy={busy}";
                _logger?.LogInformation("[VectorStore] Optimize: {Detail}", detail);
                return new VectorCacheMaintenanceResult("optimize", true, detail);
            }
            finally
            {
                _pool.Return(conn);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[VectorStore] Optimize failed");
            return new VectorCacheMaintenanceResult("optimize", false, ex.Message);
        }
    }

    public async Task<VectorCacheMaintenanceResult> VacuumAsync()
    {
        if (_dbPath is null)
            return new VectorCacheMaintenanceResult("vacuum", true, "In-memory store — nothing to do.");

        var sizeBefore = SafeFileSize(_dbPath);
        try
        {
            var conn = _pool.Rent();
            try
            {
                using var cmd = new SqliteCommand("VACUUM");
                cmd.Connection = conn;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                var sizeAfter = SafeFileSize(_dbPath);
                var detail = $"Database rebuilt: {sizeBefore:N0} → {sizeAfter:N0} bytes " +
                             $"(freed {sizeBefore - sizeAfter:N0} bytes)";
                _logger?.LogInformation("[VectorStore] Vacuum: {Detail}", detail);
                return new VectorCacheMaintenanceResult("vacuum", true, detail);
            }
            finally
            {
                _pool.Return(conn);
            }
        }
        catch (Exception ex)
        {
            // e.g. SQLITE_BUSY while another connection holds a read snapshot
            _logger?.LogError(ex, "[VectorStore] Vacuum failed");
            return new VectorCacheMaintenanceResult("vacuum", false, ex.Message);
        }
    }

    private static long SafeFileSize(string? path)
    {
        try { return path is null || !File.Exists(path) ? 0 : new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static DateTime? ParseUtc(string? value)
    {
        return value is not null
            && DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var result)
            ? result
            : null;
    }

    private static DateTime? MinUtc(DateTime? current, string? value)
    {
        var parsed = ParseUtc(value);
        return parsed is null ? current : (current is null || parsed < current ? parsed : current);
    }

    private static DateTime? MaxUtc(DateTime? current, string? value)
    {
        var parsed = ParseUtc(value);
        return parsed is null ? current : (current is null || parsed > current ? parsed : current);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool.Dispose();
        GC.SuppressFinalize(this);
    }

    // ─── Serialisation helpers ───

    private static byte[] Serialize(float[] vector)
    {
        int dim = vector.Length;
        int totalBytes = 4 + dim * 4;
        var bytes = new byte[totalBytes];

        var dimBytes = BitConverter.GetBytes(dim);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(dimBytes);
        dimBytes.CopyTo(bytes, 0);

        for (int i = 0; i < dim; i++)
        {
            var fBytes = BitConverter.GetBytes(vector[i]);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(fBytes);
            fBytes.CopyTo(bytes, 4 + i * 4);
        }

        return bytes;
    }

    private static float[] Deserialize(byte[] blob)
    {
        if (blob.Length < 8)
            throw new InvalidOperationException("Invalid embedding blob: too short.");

        var dimBytes = new byte[4];
        Array.Copy(blob, 0, dimBytes, 0, 4);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(dimBytes);
        int dim = BitConverter.ToInt32(dimBytes, 0);

        int expected = 4 + dim * 4;
        if (blob.Length != expected)
            throw new InvalidOperationException($"Invalid embedding blob: expected {expected} bytes, got {blob.Length}.");

        var result = new float[dim];
        for (int i = 0; i < dim; i++)
        {
            var fBytes = new byte[4];
            Array.Copy(blob, 4 + i * 4, fBytes, 0, 4);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(fBytes);
            result[i] = BitConverter.ToSingle(fBytes, 0);
        }

        return result;
    }
}
