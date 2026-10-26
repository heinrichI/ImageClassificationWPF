using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;

namespace ImageClassification.VectorStore;

/// <summary>
/// SQLite-based vector embedding cache.
/// Stores float[] as BLOB: 4-byte LE int32 (dim) + dim*4 bytes of float data.
/// Three tables: "embeddings" — generic per-file cache of the other features;
/// "archive_embeddings" — one row per archive holding the packed vector array of ALL
/// its pages (the row's presence = the archive is fully cached, so it doubles as the
/// "fully cached" marker); "cover_embeddings" — one row per archive holding the cover
/// vector. Uses SqliteConnectionPool so that concurrent reads do not block each other.
/// </summary>
internal sealed class SqliteVectorStore : IVectorStore
{
    private readonly SqliteConnectionPool _pool;
    private readonly string? _dbPath;
    private readonly ILogger<SqliteVectorStore>? _logger;
    private bool _disposed;
    private Task? _migrationTask;

    public string? DatabasePath => _dbPath;

    /// <summary>
    /// Completes when the one-time legacy migration has finished (or was skipped).
    /// The migration runs on a background thread so app startup and the UI are never
    /// blocked by it; the comic-search methods await this before touching migrated data.
    /// </summary>
    public Task WhenReady => _migrationTask ?? Task.CompletedTask;

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

            // "archive_embeddings": one row per archive holding the packed vector array
            // of ALL its pages (page 0 first). The row records its own page count and
            // file metadata, so a fresh row means the archive is fully cached — the row
            // IS the marker; no separate marker table is needed.
            using var archives = new SqliteCommand(@"
                CREATE TABLE IF NOT EXISTS archive_embeddings (
                    model_name    TEXT    NOT NULL,
                    archive_path  TEXT    NOT NULL,
                    page_count    INTEGER NOT NULL,
                    last_modified TEXT    NOT NULL,
                    file_size     INTEGER NOT NULL,
                    created_at    TEXT    NOT NULL,
                    embedding     BLOB    NOT NULL,
                    PRIMARY KEY (model_name, archive_path)
                )");
            archives.Connection = conn;
            archives.ExecuteNonQuery();

            // "cover_embeddings": one row per archive holding the single cover vector.
            using var covers = new SqliteCommand(@"
                CREATE TABLE IF NOT EXISTS cover_embeddings (
                    model_name    TEXT    NOT NULL,
                    archive_path  TEXT    NOT NULL,
                    last_modified TEXT    NOT NULL,
                    file_size     INTEGER NOT NULL,
                    created_at    TEXT    NOT NULL,
                    embedding     BLOB    NOT NULL,
                    PRIMARY KEY (model_name, archive_path)
                )");
            covers.Connection = conn;
            covers.ExecuteNonQuery();

            // Enable WAL mode for better concurrent reads
            using var wal = new SqliteCommand("PRAGMA journal_mode=WAL;");
            wal.Connection = conn;
            wal.ExecuteNonQuery();
        }
        finally
        {
            _pool.Return(conn);
        }

        // The one-time legacy migration repacks hundreds of MB of vector blobs and can
        // take minutes on a large cache — it must not block startup. Run it in the
        // background; the migrated-data methods (and the search service) await WhenReady.
        _migrationTask = Task.Run(() =>
        {
            try
            {
                MigrateLegacyEmbeddings();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "[VectorStore] Legacy migration failed — the legacy comic cache will be re-encoded on the next search");
            }
        });
    }

    /// <summary>
    /// One-time migration from the legacy single-table layout, where all comic rows lived
    /// in "embeddings": per-page rows "{path}|page=N" plus bare-path cover rows, while a
    /// separate "archive_cache_markers" table recorded which archives were fully cached.
    /// The page rows of every archive the marker table attests complete are packed into a
    /// single "archive_embeddings" row (the row doubles as the marker); cover rows move
    /// to "cover_embeddings"; the marker table is dropped. Partial archives without a
    /// marker lose their page rows — they get re-encoded on the next search. Everything
    /// runs in one transaction, so any failure rolls back and the migration simply
    /// retries on the next startup.
    /// </summary>
    private void MigrateLegacyEmbeddings()
    {
        var conn = _pool.Rent();
        try
        {
            // Fast path first: the migration commits as ONE transaction (all-or-nothing),
            // so non-empty new tables mean it already completed. This cheap check (~ms —
            // two small index counts) must run BEFORE the legacy trigger scan, which is a
            // full-table scan (leading-wildcard LIKE — no index) and on a large cache costs
            // seconds on EVERY app startup for no reason.
            using (var fresh = new SqliteCommand(
                       "SELECT (SELECT COUNT(*) FROM archive_embeddings) + (SELECT COUNT(*) FROM cover_embeddings)"))
            {
                fresh.Connection = conn;
                if (Convert.ToInt64(fresh.ExecuteScalar()) > 0)
                {
                    _logger?.LogDebug("[VectorStore] Migration: already migrated (new tables populated) — skipping");
                    return;
                }
            }

            // New tables are empty — the legacy trigger scan decides between
            // "nothing to migrate" and "run the full migration".
            using (var trigger = new SqliteCommand(
                       "SELECT COUNT(*) FROM embeddings WHERE " +
                       "file_path LIKE '%|page=%' OR " +
                       "LOWER(file_path) LIKE '%.cbz' OR LOWER(file_path) LIKE '%.cbr' OR " +
                       "LOWER(file_path) LIKE '%.cb7' OR LOWER(file_path) LIKE '%.cbt'"))
            {
                trigger.Connection = conn;
                if (Convert.ToInt64(trigger.ExecuteScalar()) == 0)
                {
                    // No legacy comic rows — just drop the obsolete marker table when present.
                    using var drop = new SqliteCommand("DROP TABLE IF EXISTS archive_cache_markers");
                    drop.Connection = conn;
                    drop.ExecuteNonQuery();
                    return;
                }
            }

            // Known-complete archives per the legacy marker table: (model, archive) → page count.
            // Loaded before the transaction starts (read-only snapshot; the table is only
            // dropped at the very end of the migration).
            var completeArchives = new Dictionary<(string Model, string Archive), int>();
            {
                using var markers = new SqliteCommand(
                    "SELECT model_name, archive_path, page_count FROM archive_cache_markers");
                markers.Connection = conn;
                using var reader = markers.ExecuteReader();
                while (reader.Read())
                    completeArchives[(reader.GetString(0), reader.GetString(1))] = reader.GetInt32(2);
            }

            using var tx = conn.BeginTransaction();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Single streaming pass over ALL legacy page rows — one scan of the table.
            // (A per-archive query would NOT be an index range scan here, so on a large
            // cache it degrades to thousands of full-table scans. One pass scales linearly.)
            // Page blobs of marker-attested archives are buffered in memory; everything
            // else is counted and dropped. Peak memory ≈ size of the comic page cache.
            var groups = new Dictionary<(string Model, string Archive), LegacyGroup>();
            var pageRowsTotal = 0;
            using (var pageCmd = new SqliteCommand(
                       @"SELECT model_name, file_path, last_modified, file_size, created_at, embedding
                         FROM embeddings
                         WHERE file_path LIKE '%|page=%'
                         ORDER BY model_name, file_path"))
            {
                pageCmd.Connection = conn;
                pageCmd.Transaction = tx;
                using var reader = pageCmd.ExecuteReader();
                while (reader.Read())
                {
                    pageRowsTotal++;
                    var filePath = reader.GetString(1);
                    var sep = filePath.IndexOf("|page=", StringComparison.Ordinal);
                    if (sep < 0 || !int.TryParse(filePath[(sep + 6)..], out var page))
                        continue; // malformed legacy key — treat like a partial archive

                    var key = (Model: reader.GetString(0), Archive: filePath[..sep]);
                    if (!groups.TryGetValue(key, out var group))
                    {
                        group = new LegacyGroup
                        {
                            Model = key.Model,
                            Archive = key.Archive,
                            LastModified = reader.GetString(2),
                            FileSize = reader.GetInt64(3),
                            CreatedAt = reader.GetString(4),
                            // Buffer blobs only for marker-attested archives; partial
                            // archives are dropped without keeping their data in memory.
                            Pages = completeArchives.ContainsKey(key) ? new Dictionary<int, byte[]>() : null
                        };
                        groups[key] = group;
                    }
                    if (group.Pages is { } pages)
                        pages[page] = reader.GetFieldValue<byte[]>(5);
                }
            } // reader closed — the connection is free for the INSERTs below

            int archivesPacked = 0;
            int archivesIncomplete = 0;
            foreach (var group in groups.Values)
            {
                if (group.Pages is null
                    || !completeArchives.TryGetValue((group.Model, group.Archive), out var expected))
                {
                    archivesIncomplete++;
                    continue;
                }

                var ok = group.Pages.Count >= expected;
                for (var p = 0; ok && p < expected; p++)
                    ok = group.Pages.ContainsKey(p);
                if (!ok)
                {
                    archivesIncomplete++;
                    continue;
                }

                using var packed = new MemoryStream();
                for (var p = 0; p < expected; p++)
                    packed.Write(group.Pages[p], 0, group.Pages[p].Length);

                using var ins = new SqliteCommand(@"
                    INSERT OR REPLACE INTO archive_embeddings
                        (model_name, archive_path, page_count, last_modified, file_size, created_at, embedding)
                    VALUES (@mn, @ap, @pc, @lm, @fs, @ca, @emb)");
                ins.Connection = conn;
                ins.Transaction = tx;
                ins.Parameters.AddWithValue("@mn", group.Model);
                ins.Parameters.AddWithValue("@ap", group.Archive);
                ins.Parameters.AddWithValue("@pc", expected);
                ins.Parameters.AddWithValue("@lm", group.LastModified);
                ins.Parameters.AddWithValue("@fs", group.FileSize);
                ins.Parameters.AddWithValue("@ca", group.CreatedAt);
                ins.Parameters.AddWithValue("@emb", packed.ToArray());
                ins.ExecuteNonQuery();
                archivesPacked++;
            }

            // Cover rows (bare archive paths) move to cover_embeddings.
            int coversMoved;
            using (var covers = new SqliteCommand(@"
                INSERT OR REPLACE INTO cover_embeddings
                    (model_name, archive_path, last_modified, file_size, created_at, embedding)
                SELECT model_name, file_path, last_modified, file_size, created_at, embedding
                FROM embeddings
                WHERE file_path NOT LIKE '%|page=%'
                  AND (LOWER(file_path) LIKE '%.cbz' OR LOWER(file_path) LIKE '%.cbr'
                       OR LOWER(file_path) LIKE '%.cb7' OR LOWER(file_path) LIKE '%.cbt')"))
            {
                covers.Connection = conn;
                covers.Transaction = tx;
                coversMoved = covers.ExecuteNonQuery();
            }

            // Remove every legacy comic row from the generic table: the packed page
            // rows, the dropped partials and the moved covers — in one go.
            using (var del = new SqliteCommand(@"
                DELETE FROM embeddings
                WHERE file_path LIKE '%|page=%'
                   OR LOWER(file_path) LIKE '%.cbz' OR LOWER(file_path) LIKE '%.cbr'
                   OR LOWER(file_path) LIKE '%.cb7' OR LOWER(file_path) LIKE '%.cbt'"))
            {
                del.Connection = conn;
                del.Transaction = tx;
                del.ExecuteNonQuery();
            }

            using (var drop = new SqliteCommand("DROP TABLE IF EXISTS archive_cache_markers"))
            {
                drop.Connection = conn;
                drop.Transaction = tx;
                drop.ExecuteNonQuery();
            }

            tx.Commit();

            _logger?.LogInformation(
                "[VectorStore] Legacy migration: {Packed} archive row(s) packed, {Covers} cover row(s) moved, " +
                "{PageRows} page row(s) processed, {Incomplete} archive(s) incomplete (re-encoded on next search), in {Ms} ms",
                archivesPacked, coversMoved, pageRowsTotal, archivesIncomplete, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // Best-effort: the store must stay usable. The transaction rolled back, so
            // the legacy rows are still in "embeddings" and this retries on next startup.
            _logger?.LogError(ex,
                "[VectorStore] Legacy migration failed — the legacy comic cache will be re-encoded on the next search");
        }
        finally
        {
            _pool.Return(conn);
        }
    }

    /// <summary>Metadata of one legacy (model, archive) page group, gathered during migration.</summary>
    private sealed class LegacyGroup
    {
        public string Model { get; init; } = string.Empty;
        public string Archive { get; init; } = string.Empty;
        public string LastModified { get; init; } = string.Empty;
        public long FileSize { get; init; }
        public string CreatedAt { get; init; } = string.Empty;

        /// <summary>Page blobs for marker-attested archives; null for archives that are dropped.</summary>
        public Dictionary<int, byte[]>? Pages { get; set; }
    }

    public async Task SaveAsync(string modelName, string filePath,
        DateTime lastModified, long fileSize, float[] embedding)
    {
        // An empty embedding would poison the cache row: every later probe would
        // "hit" a zero vector that scores 0 against everything.
        Debug.Assert(embedding is { Length: > 0 }, "embedding must not be empty");
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

        // path → probe entry (first occurrence wins) — lets every scanned row be
        // classified in O(1) without any per-row SQL work.
        var probeInfo = new Dictionary<string, (DateTime Lm, long Size)>(entries.Count);
        foreach (var (path, lm, size) in entries)
            probeInfo.TryAdd(path, (lm, size));

        // Sequential scan of the model's cached rows, classified in C#.
        // ProbeBenchmark (benchmarks/ProbeBenchmark) measured this against every
        // parameterized variant on the app's real 3.5 GB cache:
        //   scan: 0.09–3.6 s  vs  IN-500: 1.2–4.2 s  vs  temp-table JOIN: 2.3–9.0 s
        // across cache sizes 8.8k → 300k rows and probe sets of 300,479 files.
        // The query carries a single parameter, so SQLite's host-variable limit
        // (999/32766) can never bite, and the probe cost scales with the number of
        // CACHED rows — not with the (much larger) number of probed files.
        var conn = _pool.Rent();
        try
        {
            using var cmd = new SqliteCommand(
                "SELECT file_path, last_modified, file_size, embedding " +
                "FROM embeddings WHERE model_name = @mn", conn);
            cmd.Parameters.AddWithValue("@mn", modelName);

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var filePath = reader.GetString(0);
                if (!probeInfo.TryGetValue(filePath, out var info))
                    continue; // row for a file outside this probe set

                if (reader.GetString(1) == info.Lm.ToString("O") && reader.GetInt64(2) == info.Size)
                    results[filePath] = Deserialize(reader.GetFieldValue<byte[]>(3));
                // stale: file changed since the row was written → left null → re-encoded
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
        await WhenReady;
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

            // Archive and cover rows go with their generic counterparts.
            using var archives = new SqliteCommand(
                modelName is null
                    ? "DELETE FROM archive_embeddings"
                    : "DELETE FROM archive_embeddings WHERE model_name = @mn");
            archives.Connection = conn;
            if (modelName is not null)
                archives.Parameters.AddWithValue("@mn", modelName);
            await archives.ExecuteNonQueryAsync().ConfigureAwait(false);

            using var covers = new SqliteCommand(
                modelName is null
                    ? "DELETE FROM cover_embeddings"
                    : "DELETE FROM cover_embeddings WHERE model_name = @mn");
            covers.Connection = conn;
            if (modelName is not null)
                covers.Parameters.AddWithValue("@mn", modelName);
            await covers.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _pool.Return(conn);
        }
    }

    public async Task<(int PageCount, float[][] Pages)?> GetArchiveAsync(
        string modelName, string archivePath, DateTime lastModifiedUtc, long fileSize)
    {
        await WhenReady;
        var conn = _pool.Rent();
        try
        {
            using var cmd = new SqliteCommand(@"
                SELECT page_count, last_modified, file_size, embedding
                FROM archive_embeddings
                WHERE model_name = @mn AND archive_path = @ap");

            cmd.Connection = conn;
            cmd.Parameters.AddWithValue("@mn", modelName);
            cmd.Parameters.AddWithValue("@ap", archivePath);

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
            {
                _logger?.LogDebug(
                    "Archive miss: {ModelName}/{ArchivePath} not found", modelName, archivePath);
                return null;
            }

            var storedLm = reader.GetString(1);
            var storedFs = reader.GetInt64(2);
            var currentLm = lastModifiedUtc.ToString("O");

            if (storedLm != currentLm || storedFs != fileSize)
            {
                _logger?.LogDebug(
                    "Archive stale: {ModelName}/{ArchivePath} " +
                    "stored(lm={StoredLm},fs={StoredFs}) != " +
                    "current(lm={CurrentLm},fs={FileSize})",
                    modelName, archivePath, storedLm, storedFs, currentLm, fileSize);
                return null;
            }

            var pageCount = reader.GetInt32(0);
            var pages = DeserializePages(reader.GetFieldValue<byte[]>(3));
            if (pages.Length != pageCount)
            {
                // Defensive: the row is self-describing — an inconsistent row must
                // behave like a miss, not corrupt the search results.
                _logger?.LogWarning(
                    "Archive row inconsistent: {ModelName}/{ArchivePath} declares {Declared} page(s), blob holds {Actual} — treated as a miss",
                    modelName, archivePath, pageCount, pages.Length);
                return null;
            }

            return (pageCount, pages);
        }
        finally
        {
            _pool.Return(conn);
        }
    }

    public async Task SaveArchiveAsync(string modelName, string archivePath, int pageCount,
        DateTime lastModifiedUtc, long fileSize, float[][] pages)
    {
        await WhenReady;
        if (pageCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageCount), "An archive must contain at least one page.");
        if (pages is null || pages.Length != pageCount)
            throw new ArgumentException(
                $"Expected exactly {pageCount} page vectors, got {(pages is null ? "null" : pages.Length.ToString())}.",
                nameof(pages));
        if (pages.Any(p => p is null))
            throw new ArgumentException("Page vectors must not be null.", nameof(pages));

        var conn = _pool.Rent();
        try
        {
            using var cmd = new SqliteCommand(@"
                INSERT OR REPLACE INTO archive_embeddings
                    (model_name, archive_path, page_count, last_modified, file_size, embedding, created_at)
                VALUES (@mn, @ap, @pc, @lm, @fs, @emb, @ca)");

            cmd.Connection = conn;
            cmd.Parameters.AddWithValue("@mn", modelName);
            cmd.Parameters.AddWithValue("@ap", archivePath);
            cmd.Parameters.AddWithValue("@pc", pageCount);
            cmd.Parameters.AddWithValue("@lm", lastModifiedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@fs", fileSize);
            cmd.Parameters.AddWithValue("@emb", SerializePages(pages));
            cmd.Parameters.AddWithValue("@ca", DateTime.UtcNow.ToString("O"));

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _pool.Return(conn);
        }
    }

    public async Task<float[]?> GetCoverAsync(string modelName, string archivePath,
        DateTime lastModifiedUtc, long fileSize)
    {
        await WhenReady;
        var conn = _pool.Rent();
        try
        {
            using var cmd = new SqliteCommand(@"
                SELECT embedding, last_modified, file_size
                FROM cover_embeddings
                WHERE model_name = @mn AND archive_path = @ap");

            cmd.Connection = conn;
            cmd.Parameters.AddWithValue("@mn", modelName);
            cmd.Parameters.AddWithValue("@ap", archivePath);

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (await reader.ReadAsync().ConfigureAwait(false))
            {
                var storedLm = reader.GetString(1);
                var storedFs = reader.GetInt64(2);
                var currentLm = lastModifiedUtc.ToString("O");

                if (storedLm == currentLm && storedFs == fileSize)
                    return Deserialize(reader.GetFieldValue<byte[]>(0));

                _logger?.LogDebug(
                    "Cover stale: {ModelName}/{ArchivePath} " +
                    "stored(lm={StoredLm},fs={StoredFs}) != " +
                    "current(lm={CurrentLm},fs={FileSize})",
                    modelName, archivePath, storedLm, storedFs, currentLm, fileSize);
            }
            else
            {
                _logger?.LogDebug(
                    "Cover miss: {ModelName}/{ArchivePath} not found", modelName, archivePath);
            }

            return null;
        }
        finally
        {
            _pool.Return(conn);
        }
    }

    public async Task SaveCoverAsync(string modelName, string archivePath,
        DateTime lastModifiedUtc, long fileSize, float[] embedding)
    {
        await WhenReady;
        var conn = _pool.Rent();
        try
        {
            using var cmd = new SqliteCommand(@"
                INSERT OR REPLACE INTO cover_embeddings
                    (model_name, archive_path, last_modified, file_size, embedding, created_at)
                VALUES (@mn, @ap, @lm, @fs, @emb, @ca)");

            cmd.Connection = conn;
            cmd.Parameters.AddWithValue("@mn", modelName);
            cmd.Parameters.AddWithValue("@ap", archivePath);
            cmd.Parameters.AddWithValue("@lm", lastModifiedUtc.ToString("O"));
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

    public async Task<VectorCacheInfo> GetCacheInfoAsync()
    {
        await WhenReady;
        long total = 0, covers = 0, archives = 0;
        DateTime? oldest = null, newest = null;
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var conn = _pool.Rent();
        try
        {
            // All three cache tables, aggregated per model.
            foreach (var table in new[] { "embeddings", "cover_embeddings", "archive_embeddings" })
            {
                using var cmd = new SqliteCommand($@"
                    SELECT model_name,
                           COUNT(*),
                           MIN(created_at),
                           MAX(created_at)
                    FROM {table}
                    GROUP BY model_name");
                cmd.Connection = conn;

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    models.Add(reader.GetString(0));
                    var count = reader.GetInt64(1);
                    total += count;
                    if (table == "cover_embeddings")
                        covers += count;
                    else if (table == "archive_embeddings")
                        archives += count;
                    oldest = MinUtc(oldest, reader.GetString(2));
                    newest = MaxUtc(newest, reader.GetString(3));
                }
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
            covers,
            archives,
            oldest,
            newest,
            string.Join(", ", models));
    }

    public async Task<VectorCacheMaintenanceResult> OptimizeAsync()
    {
        if (_dbPath is null)
            return new VectorCacheMaintenanceResult("optimize", true, "In-memory store — nothing to do.");
        await WhenReady;

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

    public async Task<VectorCacheMaintenanceResult> VacuumAsync(
        IProgress<VectorCacheMaintenanceStatus>? progress = null,
        CancellationToken ct = default)
    {
        if (_dbPath is null)
            return new VectorCacheMaintenanceResult("vacuum", true, "In-memory store — nothing to do.");
        // Hop off the caller's thread: the UI invokes this from the dispatcher, and even
        // the synchronous preamble (connection setup) must not block the window.
        // NOTE: Task.Delay(0) is a pre-completed task (zero fast path) and would NOT hop
        // threads — Task.Run always posts to the thread pool and is the guaranteed hop.
        await Task.Run(() => { }).ConfigureAwait(false);
        await WhenReady.ConfigureAwait(false);

        var sizeBefore = SafeFileSize(_dbPath);
        // VACUUM takes an EXCLUSIVE lock and SQLite cannot report its progress. It runs on a
        // dedicated (non-pooled) connection so the pool stays usable; cancelling the token
        // interrupts the statement and SQLite rolls the whole rebuild back — the database is
        // never corrupted either way. Other store operations must be quiesced by the caller
        // while this runs (the UI shows it in a modal window and blocks searches).
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using var cmd = new SqliteCommand("VACUUM")
            {
                Connection = conn,
                CommandTimeout = 0 // multi-gigabyte rebuilds outlive any sane statement timeout
            };

            const string phase = "Rebuilding database file… this can take several minutes; Interrupt rolls the work back";
            progress?.Report(new VectorCacheMaintenanceStatus(phase, 0, 0, 0, true));

            var work = cmd.ExecuteNonQueryAsync(ct);

            // Indeterminate ticks while the statement runs (VACUUM has no progress hook).
            _ = Task.Run(async () =>
                {
                    while (!work.IsCompleted)
                    {
                        try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        progress?.Report(new VectorCacheMaintenanceStatus(phase, 0, 0, 0, true));
                    }
                }, ct);

            try
            {
                await work.ConfigureAwait(false);
                var sizeAfter = SafeFileSize(_dbPath);
                var detail = $"Database rebuilt: {sizeBefore:N0} → {sizeAfter:N0} bytes " +
                             $"(freed {sizeBefore - sizeAfter:N0} bytes)";
                _logger?.LogInformation("[VectorStore] Vacuum: {Detail}", detail);
                return new VectorCacheMaintenanceResult("vacuum", true, detail);
            }
            catch (OperationCanceledException)
            {
                var detail = "Interrupted — the rebuild was rolled back, the database is unchanged.";
                _logger?.LogInformation("[VectorStore] {Detail}", detail);
                return new VectorCacheMaintenanceResult("vacuum", false, detail);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 10) // SQLITE_INTERRUPT
            {
                var detail = "Interrupted — the rebuild was rolled back, the database is unchanged.";
                _logger?.LogInformation("[VectorStore] {Detail}", detail);
                return new VectorCacheMaintenanceResult("vacuum", false, detail);
            }
        }
        catch (OperationCanceledException)
        {
            return new VectorCacheMaintenanceResult("vacuum", false,
                "Cancelled before start — the database is unchanged.");
        }
        catch (Exception ex)
        {
            // e.g. SQLITE_BUSY while another connection holds a read snapshot
            _logger?.LogError(ex, "[VectorStore] Vacuum failed");
            return new VectorCacheMaintenanceResult("vacuum", false, ex.Message);
        }
    }

    public async Task<VectorCacheMaintenanceResult> PruneMissingFilesAsync(
        string? modelName = null,
        IProgress<VectorCacheMaintenanceStatus>? progress = null,
        CancellationToken ct = default)
    {
        if (_dbPath is null)
            return new VectorCacheMaintenanceResult("prune", true, "In-memory store — nothing to do.");
        // Hop off the caller's thread: the UI invokes this from the dispatcher, and this
        // body includes synchronous work (pool Rent, MDS "async" calls that run on the
        // calling thread, disk probes) that must not block the window.
        // NOTE: Task.Delay(0) is a pre-completed task (zero fast path) and would NOT hop
        // threads — Task.Run always posts to the thread pool and is the guaranteed hop.
        await Task.Run(() => { }).ConfigureAwait(false);
        await WhenReady.ConfigureAwait(false);

        try
        {
            ct.ThrowIfCancellationRequested();

            progress?.Report(new VectorCacheMaintenanceStatus(
                "Collecting cache entries from the database…", 0, 0, 0, true));

            // 1. Collect every distinct file path across the three cache tables
            //    (optionally scoped to one model). Generic-table keys may carry a legacy
            //    "|page=N" suffix — mapped to their base file below.
            var basePathByKey = new Dictionary<string, string>(StringComparer.Ordinal);
            var basePaths = new HashSet<string>(StringComparer.Ordinal);
            var collectStart = Environment.TickCount;
            {
                var conn = _pool.Rent();
                try
                {
                    using var cmd = new SqliteCommand(
                        modelName is null
                            ? "SELECT DISTINCT file_path FROM embeddings"
                            : "SELECT DISTINCT file_path FROM embeddings WHERE model_name = @mn");
                    cmd.Connection = conn;
                    if (modelName is not null)
                        cmd.Parameters.AddWithValue("@mn", modelName);

                    using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        var key = reader.GetString(0);
                        var basePath = BaseFilePath(key);
                        basePathByKey[key] = basePath;
                        basePaths.Add(basePath);
                    }

                    using var archives = new SqliteCommand(
                        modelName is null
                            ? "SELECT DISTINCT archive_path FROM archive_embeddings"
                            : "SELECT DISTINCT archive_path FROM archive_embeddings WHERE model_name = @mn");
                    archives.Connection = conn;
                    if (modelName is not null)
                        archives.Parameters.AddWithValue("@mn", modelName);
                    using var areader = await archives.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await areader.ReadAsync().ConfigureAwait(false))
                        basePaths.Add(areader.GetString(0));

                    using var covers = new SqliteCommand(
                        modelName is null
                            ? "SELECT DISTINCT archive_path FROM cover_embeddings"
                            : "SELECT DISTINCT archive_path FROM cover_embeddings WHERE model_name = @mn");
                    covers.Connection = conn;
                    if (modelName is not null)
                        covers.Parameters.AddWithValue("@mn", modelName);
                    using var cread = await covers.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await cread.ReadAsync().ConfigureAwait(false))
                        basePaths.Add(cread.GetString(0));
                }
                finally
                {
                    _pool.Return(conn);
                }
            }

            if (basePaths.Count == 0)
                return new VectorCacheMaintenanceResult("prune", true, "Cache is empty — nothing to do.");

            _logger?.LogInformation(
                "[VectorStore] Prune: collected {Count} distinct path(s) from the cache in {Ms} ms",
                basePaths.Count, Environment.TickCount - collectStart);

            // 3. Disk-root state is re-validated periodically (not just once per run):
            //    a disk that drops offline mid-run must stop being probed — a disk
            //    probe on a dying volume can hang for minutes, and one hung probe must
            //    not stall the whole check.
            var rootChecked = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            bool RootConnected(string root) => rootChecked.GetOrAdd(root, _ => IsRootConnected(root));

            // 4. A file counts as missing only if its disk is connected AND the file is
            //    gone. Files on disconnected (removable / offline) disks are left
            //    untouched — the file may well reappear once the disk is reconnected.
            //
            //    The check runs per DIRECTORY, not per file: paths are grouped by their
            //    parent directory; a missing directory makes every path in it missing at
            //    once (no disk probes), an existing one needs a single EnumerateFiles
            //    instead of one File.Exists per file. On huge prune jobs that is the
            //    difference between minutes and seconds (O(directories) instead of
            //    O(files)); per-file File.Exists remains only as a fallback (files on a
            //    drive root, enumeration errors). Groups run on a few pool threads, so
            //    one worker stuck on a dying volume cannot freeze the others or the UI,
            //    and a time-based tick keeps the progress bar moving on slow disks.
            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var basePath in basePaths)
            {
                var parent = SafeGetDirectoryName(basePath);
                if (!groups.TryGetValue(parent, out var list))
                    groups[parent] = list = new List<string>();
                list.Add(basePath);
            }
            var groupList = groups.ToList();

            var missingBases = new HashSet<string>(StringComparer.Ordinal);
            var offlineRoots = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            void CheckGroup(string parent, IReadOnlyList<string> paths)
            {
                bool dirExists;
                try { dirExists = Directory.Exists(parent); }
                catch { dirExists = false; }

                if (!dirExists)
                {
                    // The whole directory is gone — every path in it is missing, no probes.
                    lock (missingBases) missingBases.UnionWith(paths);
                    return;
                }

                // Files sitting directly on a drive root: enumerating the root is not
                // worth it — per-file probes instead.
                if (IsDriveRoot(parent))
                {
                    foreach (var p in paths)
                        if (!File.Exists(p))
                            lock (missingBases) missingBases.Add(p);
                    return;
                }

                try
                {
                    // EnumerateFiles returns full paths — compare by file name (NTFS is
                    // case-insensitive, so cache keys differing in case still match).
                    var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in Directory.EnumerateFiles(parent))
                        existing.Add(Path.GetFileName(name)!);
                    foreach (var p in paths)
                        if (!existing.Contains(Path.GetFileName(p)))
                            lock (missingBases) missingBases.Add(p);
                }
                catch
                {
                    // Enumeration failed (e.g. access denied) — per-file fallback.
                    foreach (var p in paths)
                        if (!File.Exists(p))
                            lock (missingBases) missingBases.Add(p);
                }
            }

            var checkedFiles = 0;
            int lastReport = Environment.TickCount;
            int lastLog = Environment.TickCount;
            int checkStart = Environment.TickCount;

            // Diagnostics (ICS_LOG_FILE): what we are about to check.
            var topRoots = basePaths
                .GroupBy(SafePathRoot)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => $"{g.Key}={g.Count()}")
                .ToList();
            _logger?.LogInformation(
                "[VectorStore] Prune: {Total} distinct path(s) in {Groups} directory group(s); roots: {Roots}",
                basePaths.Count, groupList.Count, string.Join(", ", topRoots));

            var workerCount = Math.Max(1, Math.Min(4, Environment.ProcessorCount));
            var chunk = Math.Max(1, (groupList.Count + workerCount - 1) / workerCount);
            var workers = new List<Task>();
            for (var w = 0; w < workerCount; w++)
            {
                var start = w * chunk;
                if (start >= groupList.Count) break;
                var end = Math.Min(groupList.Count, start + chunk);

                workers.Add(Task.Run(() =>
                {
                    for (var g = start; g < end; g++)
                    {
                        var parent = groupList[g].Key;
                        var paths = groupList[g].Value;
                        ct.ThrowIfCancellationRequested();

                        // Periodically (every 64 groups) re-validate all root states: a
                        // disk that dropped offline since the last validation is skipped
                        // from here on (its entries are kept).
                        if (g > 0 && g % 64 == 0)
                            rootChecked.Clear();

                        var root = SafePathRoot(parent);
                        if (root.Length == 0)
                        {
                            // Unrooted path — cannot verify, keep the entry.
                            Interlocked.Add(ref checkedFiles, paths.Count);
                            continue;
                        }

                        if (!RootConnected(root))
                        {
                            lock (offlineRoots) offlineRoots.Add(root);
                            Interlocked.Add(ref checkedFiles, paths.Count);
                            continue;
                        }

                        // Time the probe: a group that takes > 10 s is a struggling
                        // volume and is worth a log line (ICS_LOG_FILE).
                        var probeStart = Environment.TickCount;
                        CheckGroup(parent, paths);
                        Interlocked.Add(ref checkedFiles, paths.Count);
                        var probeMs = Environment.TickCount - probeStart;
                        if (probeMs > 10_000)
                            _logger?.LogWarning(
                                "[VectorStore] Prune: slow group {Group} ({Count} file(s)) took {Ms} ms",
                                parent, paths.Count, probeMs);

                        // UI liveness: tick on file milestones OR whenever 500 ms passed
                        // since the last report (slow / struggling volumes).
                        var now = Environment.TickCount;
                        if (now - Interlocked.Exchange(ref lastReport, now) >= 500
                            || (checkedFiles & 0xFF) == 0)
                        {
                            int missingNow;
                            lock (missingBases) { missingNow = missingBases.Count; }
                            progress?.Report(new VectorCacheMaintenanceStatus(
                                "Checking files on disk…", checkedFiles, basePaths.Count,
                                missingNow, false));

                            // Diagnostics (ICS_LOG_FILE): progress line every ~5 s.
                            if (now - Interlocked.Exchange(ref lastLog, now) >= 5000)
                                _logger?.LogInformation(
                                    "[VectorStore] Prune progress: {Checked}/{Total} file(s) ({Pct}%), missing {Missing}, group {Group}",
                                    checkedFiles, basePaths.Count,
                                    basePaths.Count == 0 ? 0 : checkedFiles * 100 / basePaths.Count,
                                    missingNow, parent);
                        }
                    }
                }, ct));
            }
            await Task.WhenAll(workers).ConfigureAwait(false);

            _logger?.LogInformation(
                "[VectorStore] Prune: file check finished in {Ms} ms — {Missing} missing of {Total}",
                Environment.TickCount - checkStart, missingBases.Count, basePaths.Count);

            progress?.Report(new VectorCacheMaintenanceStatus(
                "Checking files on disk…", checkedFiles, basePaths.Count, missingBases.Count, false));

            var missingKeys = basePathByKey.Where(kv => missingBases.Contains(kv.Value)).Select(kv => kv.Key).ToList();

            // 5. Delete in one transaction: the exact generic keys plus the archive and
            //    cover rows of every missing file (SQLite parameter limit is 999 — chunked)
            int removedGeneric = 0, removedArchives = 0, removedCovers = 0;
            if (missingKeys.Count > 0 || missingBases.Count > 0)
            {
                progress?.Report(new VectorCacheMaintenanceStatus(
                    "Removing entries from the database…", 0, 0, missingBases.Count, true));
                var conn = _pool.Rent();
                try
                {
                    using var tx = conn.BeginTransaction();
                    var deleteStart = Environment.TickCount;

                    // Stage-based progress: the bulk delete has no per-row progress.
                    var stages = 1 + (missingKeys.Count > 0 ? 1 : 0) + (missingBases.Count > 0 ? 2 : 0);
                    var stage = 0;
                    void TickStage(string phase)
                    {
                        stage++;
                        progress?.Report(new VectorCacheMaintenanceStatus(
                            phase, stage, stages, missingBases.Count, false));
                    }

                    // BULK delete: the missing keys/bases are loaded into per-connection
                    // temp tables and each cache table is deleted by ONE statement driven
                    // by a throwaway index on the path column. The primary key is
                    // (model_name, path), so a path-only chunked IN(...) list full-scans
                    // the table once per chunk — hundreds of scans on a multi-GB cache.
                    // Everything below is in one transaction: any failure or cancellation
                    // rolls back the deletes, temp tables and throwaway indexes together.
                    if (missingKeys.Count > 0)
                    {
                        using (var mk = new SqliteCommand(
                            "CREATE TEMP TABLE prune_missing_generic (key TEXT PRIMARY KEY)", conn, tx))
                            await mk.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        await FillTempTableAsync(conn, tx, "prune_missing_generic", "key", missingKeys, ct).ConfigureAwait(false);
                    }
                    if (missingBases.Count > 0)
                    {
                        using (var mb = new SqliteCommand(
                            "CREATE TEMP TABLE prune_missing_base (path TEXT PRIMARY KEY)", conn, tx))
                            await mb.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        await FillTempTableAsync(conn, tx, "prune_missing_base", "path", missingBases.ToList(), ct).ConfigureAwait(false);
                    }
                    TickStage("Bulk delete prepared…");

                    if (missingKeys.Count > 0)
                    {
                        removedGeneric = await DeleteViaThrowawayIndexAsync(
                            conn, tx, "embeddings", "file_path", "prune_missing_generic", "key", modelName, ct)
                            .ConfigureAwait(false);
                        TickStage("Removed generic embeddings…");
                    }
                    if (missingBases.Count > 0)
                    {
                        removedArchives = await DeleteViaThrowawayIndexAsync(
                            conn, tx, "archive_embeddings", "archive_path", "prune_missing_base", "path", modelName, ct)
                            .ConfigureAwait(false);
                        removedCovers = await DeleteViaThrowawayIndexAsync(
                            conn, tx, "cover_embeddings", "archive_path", "prune_missing_base", "path", modelName, ct)
                            .ConfigureAwait(false);
                        TickStage("Removed archive and cover embeddings…");
                    }

                    tx.Commit();
                    _logger?.LogInformation(
                        "[VectorStore] Prune: bulk delete finished in {Ms} ms — embeddings {E}, archives {A}, covers {C}",
                        Environment.TickCount - deleteStart, removedGeneric, removedArchives, removedCovers);
                }
                finally
                {
                    _pool.Return(conn);
                }
            }

            var offline = offlineRoots.Count > 0
                ? $"; {offlineRoots.Count} disk(s) offline, their entries kept: {string.Join(", ", offlineRoots)}"
                : string.Empty;
            var detail = $"Scanned {basePaths.Count} file(s): " +
                         $"removed {removedGeneric} generic entr{(removedGeneric == 1 ? "y" : "ies")}, " +
                         $"{removedArchives} archive row(s) and {removedCovers} cover row(s) " +
                         $"for {missingBases.Count} missing file(s)" + offline;
            _logger?.LogInformation("[VectorStore] Prune: {Detail}", detail);
            return new VectorCacheMaintenanceResult("prune", true, detail);
        }
        catch (OperationCanceledException)
        {
            // Deletion happens only after the whole file check, in one transaction —
            // a cancellation at any point leaves the cache exactly as it was.
            var detail = "Cancelled — no changes were made.";
            _logger?.LogInformation("[VectorStore] Prune: {Detail}", detail);
            return new VectorCacheMaintenanceResult("prune", false, detail);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[VectorStore] Prune failed");
            return new VectorCacheMaintenanceResult("prune", false, ex.Message);
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

    /// <summary>
    /// Strips the "|page=N" suffix used by all-pages cache keys; covers keys (bare paths)
    /// are returned unchanged.
    /// </summary>
    internal static string BaseFilePath(string cacheKey)
    {
        var idx = cacheKey.IndexOf("|page=", StringComparison.Ordinal);
        return idx < 0 ? cacheKey : cacheKey[..idx];
    }

    /// <summary>
    /// True if the disk owning the given path root is currently connected (local drive ready,
    /// network share reachable). Unrooted paths report false. Never throws.
    /// </summary>
    internal static bool IsRootConnected(string pathRoot)
    {
        if (string.IsNullOrEmpty(pathRoot))
            return false;
        try
        {
            return new DriveInfo(pathRoot).IsReady;
        }
        catch
        {
            return false;
        }
    }

    private static string SafePathRoot(string path)
    {
        try { return Path.GetPathRoot(path) ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SafeGetDirectoryName(string path)
    {
        try { return Path.GetDirectoryName(path) ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>True for a drive-root directory ("C:\" / "C:").</summary>
    private static bool IsDriveRoot(string dir)
    {
        return dir.Length is 2 or 3 && dir[1] == ':';
    }

    /// <summary>
    /// Loads the given values into a temp table in multi-row chunks (at most 2,000 rows
    /// per statement — far below the SQLite parameter limit). The temp table must already
    /// exist on the connection; table/column are internal identifiers, not user input.
    /// </summary>
    private static async Task FillTempTableAsync(
        SqliteConnection conn, SqliteTransaction tx, string table, string column,
        IReadOnlyList<string> values, CancellationToken ct)
    {
        const int chunkSize = 2000;
        for (var i = 0; i < values.Count; i += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var part = values.Skip(i).Take(chunkSize).ToList();
            using var cmd = new SqliteCommand(
                $"INSERT INTO {table} ({column}) VALUES (" +
                string.Join("),(", part.Select((_, j) => $"@v{j}")) + ")", conn, tx);
            for (var j = 0; j < part.Count; j++)
                cmd.Parameters.AddWithValue($"@v{j}", part[j]);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes the rows of <paramref name="table"/> whose <paramref name="pathColumn"/>
    /// appears in the given temp table, driven by a throwaway index on the path column
    /// (so the delete is index-driven instead of a full table scan). The index is dropped
    /// before returning. Everything runs in the caller's transaction, so a failure or
    /// cancellation rolls the index back as well.
    /// </summary>
    private static async Task<int> DeleteViaThrowawayIndexAsync(
        SqliteConnection conn, SqliteTransaction tx, string table, string pathColumn,
        string tempTable, string tempColumn, string? modelName, CancellationToken ct)
    {
        var indexName = $"idx_prune_{table}_{pathColumn}";
        using (var create = new SqliteCommand($"CREATE INDEX {indexName} ON {table} ({pathColumn})", conn, tx))
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        int removed;
        using (var del = new SqliteCommand(
            $"DELETE FROM {table} WHERE {pathColumn} IN (SELECT {tempColumn} FROM {tempTable})" +
            (modelName is null ? string.Empty : " AND model_name = @mn"), conn, tx))
        {
            if (modelName is not null)
                del.Parameters.AddWithValue("@mn", modelName);
            removed = await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using (var drop = new SqliteCommand($"DROP INDEX {indexName}", conn, tx))
            await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return removed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Give the background migration a moment to finish cleanly; if it is still
            // running, SQLite rolls the uncommitted part back on the next open.
            _migrationTask?.Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // best effort — the store is going away anyway
        }
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

        int dim = BitConverter.ToInt32(blob, 0);
        int expected = 4 + dim * 4;
        if (blob.Length != expected)
            throw new InvalidOperationException($"Invalid embedding blob: expected {expected} bytes, got {blob.Length}.");

        var result = new float[dim];
        if (BitConverter.IsLittleEndian)
        {
            // Fast path: the blob stores little-endian floats, so the payload IS the
            // float array's memory layout — one memcpy (ProbeBenchmark measured this
            // as 3–10x faster than the per-float decode below).
            Buffer.BlockCopy(blob, 4, result, 0, dim * 4);
            return result;
        }

        // Big-endian fallback (not a real target for this app — kept for safety).
        for (int i = 0; i < dim; i++)
        {
            var fBytes = new byte[4];
            Array.Copy(blob, 4 + i * 4, fBytes, 0, 4);
            Array.Reverse(fBytes);
            result[i] = BitConverter.ToSingle(fBytes, 0);
        }
        return result;
    }

    /// <summary>
    /// Packs an array of pages into one blob: each page is a self-describing
    /// [dim][floats...] unit, so the blob is the plain concatenation of the page units.
    /// </summary>
    private static byte[] SerializePages(float[][] pages)
    {
        using var ms = new MemoryStream();
        foreach (var page in pages)
        {
            var blob = Serialize(page);
            ms.Write(blob, 0, blob.Length);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Splits a packed pages blob (see <see cref="SerializePages"/>) back into per-page vectors.
    /// </summary>
    private static float[][] DeserializePages(byte[] blob)
    {
        var pages = new List<float[]>();
        var offset = 0;
        while (offset < blob.Length)
        {
            var dimBytes = new byte[4];
            Array.Copy(blob, offset, dimBytes, 0, 4);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(dimBytes);
            int dim = BitConverter.ToInt32(dimBytes, 0);

            var start = offset + 4;
            var expected = start + dim * 4;
            if (expected > blob.Length)
                throw new InvalidOperationException(
                    $"Invalid archive blob: truncated after offset {offset}.");

            var page = new float[dim];
            for (int i = 0; i < dim; i++)
            {
                var fBytes = new byte[4];
                Array.Copy(blob, start + i * 4, fBytes, 0, 4);
                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(fBytes);
                page[i] = BitConverter.ToSingle(fBytes, 0);
            }

            pages.Add(page);
            offset = expected;
        }

        return pages.ToArray();
    }
}
