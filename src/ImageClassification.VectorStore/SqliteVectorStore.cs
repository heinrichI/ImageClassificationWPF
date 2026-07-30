using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ImageClassification.Core.Services;
using ImageClassification.Core.Interfaces;

namespace ImageClassification.VectorStore;

/// <summary>
/// SQLite-based vector embedding cache.
/// Stores float[] as BLOB: 4-byte LE int32 (dim) + dim*4 bytes of float data.
/// All public methods are serialized via SemaphoreSlim because SqliteConnection
/// is not thread-safe (shared _commands list modified on CreateCommand/Dispose).
/// </summary>
internal sealed class SqliteVectorStore : IVectorStore
{
    private readonly string _dbPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<SqliteVectorStore>? _logger;
    private SqliteConnection? _connection;
    private bool _disposed;

    public SqliteVectorStore(string? dbPath = null, ILogger<SqliteVectorStore>? logger = null)
    {
        _dbPath = dbPath ?? Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "vector_cache.db");
        _logger = logger;
        Initialize();
    }

    public void Initialize()
    {
        _lock.Wait();
        try
        {
            using var cmd = CreateCommand(@"
                CREATE TABLE IF NOT EXISTS embeddings (
                    model_name    TEXT    NOT NULL,
                    file_path     TEXT    NOT NULL,
                    last_modified TEXT    NOT NULL,
                    file_size     INTEGER NOT NULL,
                    embedding     BLOB    NOT NULL,
                    created_at    TEXT    NOT NULL,
                    PRIMARY KEY (model_name, file_path)
                )");
            cmd.ExecuteNonQuery();

            // Enable WAL mode for better concurrent reads
            using var wal = CreateCommand("PRAGMA journal_mode=WAL;");
            wal.ExecuteNonQuery();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(string modelName, string filePath,
        DateTime lastModified, long fileSize, float[] embedding)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = CreateCommand(@"
                INSERT OR REPLACE INTO embeddings
                    (model_name, file_path, last_modified, file_size, embedding, created_at)
                VALUES (@mn, @fp, @lm, @fs, @emb, @ca)");

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
            _lock.Release();
        }
    }

    public async Task<float[]?> GetAsync(string modelName, string filePath,
        DateTime lastModified, long fileSize)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var currentLm = lastModified.ToString("O");

            using var cmd = CreateCommand(@"
                SELECT embedding, last_modified, file_size
                FROM embeddings
                WHERE model_name = @mn AND file_path = @fp");

            cmd.Parameters.AddWithValue("@mn", modelName);
            cmd.Parameters.AddWithValue("@fp", filePath);

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (await reader.ReadAsync().ConfigureAwait(false))
            {
                var storedLm = reader.GetString(1);
                var storedFs = reader.GetInt64(2);

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
            _lock.Release();
        }
    }

    public async Task ClearAsync(string? modelName = null)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = CreateCommand(modelName is null
                ? "DELETE FROM embeddings"
                : "DELETE FROM embeddings WHERE model_name = @mn");

            if (modelName is not null)
                cmd.Parameters.AddWithValue("@mn", modelName);

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Dispose();
        _connection?.Dispose();
        _connection = null;
        GC.SuppressFinalize(this);
    }

    // ─── Internal helpers ───

    private SqliteConnection EnsureConnection()
    {
        if (_connection is not null && _connection.State == System.Data.ConnectionState.Open)
            return _connection;

        _connection?.Dispose();

        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();

        return _connection;
    }

    private SqliteCommand CreateCommand(string sql)
    {
        return new SqliteCommand(sql, EnsureConnection());
    }

    private static byte[] Serialize(float[] vector)
    {
        int dim = vector.Length;
        int totalBytes = 4 + dim * 4;
        var bytes = new byte[totalBytes];

        // Write dimension as LE int32
        var dimBytes = BitConverter.GetBytes(dim);
        if (BitConverter.IsLittleEndian)
            dimBytes.CopyTo(bytes, 0);
        else
        {
            Array.Reverse(dimBytes);
            dimBytes.CopyTo(bytes, 0);
        }

        // Write float values as LE
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

        // Read dimension
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
