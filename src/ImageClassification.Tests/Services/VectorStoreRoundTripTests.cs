using System.Text;
using ImageClassification.VectorStore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ImageClassification.Tests.Services;

/// <summary>
/// Integration tests for the SQLite vector cache write→read contract, run against
/// the real <see cref="SqliteVectorStore"/> with a real temporary database file.
///
/// Scenario covered: the search service saves embeddings via SaveAsync and on the
/// next app launch reads them back via GetBatchAsync — the values must round-trip
/// byte-for-byte, and a "restart" (new store instance over the same file) must see
/// what the previous process wrote.
/// </summary>
public sealed class VectorStoreRoundTripTests : IDisposable
{
    private const string Model = "ClipViTB32";

    // Mirrors the production key formats:
    //   covers mode  → bare archive path
    //   all-pages    → "{path}|page={n}"
    private const string CoversKey = @"I:\DVD_Комикс_Арчи\Archie (Comics) Double Digest v2 003 (2026) (Digital-Empire).cbz";
    private const string AllPagesKey0 = @"I:\DVD_Комикс_Арчи\Archie (Comics) Double Digest v2 003 (2026) (Digital-Empire).cbz|page=0";

    // Mirrors FileInfo values the service passes: LastWriteTimeUtc / Length
    private static readonly DateTime LastModified = new(2025, 12, 23, 8, 49, 55, DateTimeKind.Utc);
    private const long FileSize = 61_839_456;

    private readonly string _dbPath;

    public VectorStoreRoundTripTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ics_vec_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                var f = _dbPath + suffix;
                if (File.Exists(f)) File.Delete(f);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    private SqliteVectorStore CreateStore() =>
        new(_dbPath, NullLogger<SqliteVectorStore>.Instance);

    private static float[] MakeVector(int dim = 512)
    {
        var v = new float[dim];
        for (int i = 0; i < dim; i++)
            v[i] = (float)Math.Sin(i * 0.5) * 0.25f;
        return v;
    }

    [Fact]
    public async Task Save_Then_GetAsync_RoundTripsVector()
    {
        var store = CreateStore();

        var vector = MakeVector();
        await store.SaveAsync(Model, AllPagesKey0, LastModified, FileSize, vector);

        var readBack = await store.GetAsync(Model, AllPagesKey0, LastModified, FileSize);

        Assert.NotNull(readBack);
        Assert.Equal(vector, readBack!);
    }

    [Fact]
    public async Task Save_Then_GetBatchAsync_ReturnsHit_ForCoversAndAllPagesKeys()
    {
        var store = CreateStore();
        var vector = MakeVector();

        // The service saves covers under the bare path and all-pages under "|page=N"
        await store.SaveAsync(Model, CoversKey, LastModified, FileSize, vector);
        await store.SaveAsync(Model, AllPagesKey0, LastModified, FileSize, vector);

        var results = await store.GetBatchAsync(Model, new()
        {
            (CoversKey, LastModified, FileSize),
            (AllPagesKey0, LastModified, FileSize),
            (@"I:\other\missing.cbr|page=0", LastModified, FileSize) // never saved
        });

        Assert.NotNull(results[CoversKey]);
        Assert.Equal(vector, results[CoversKey]);
        Assert.NotNull(results[AllPagesKey0]);
        Assert.Equal(vector, results[AllPagesKey0]);
        Assert.Null(results[@"I:\other\missing.cbr|page=0"]);
    }

    [Fact]
    public async Task GetBatchAsync_StaleWhenLastModifiedChanged_ReturnsNull()
    {
        var store = CreateStore();
        await store.SaveAsync(Model, AllPagesKey0, LastModified, FileSize, MakeVector());

        // Archive touched/re-copied between launches → new LastWriteTimeUtc
        var newer = LastModified.AddHours(1);
        var results = await store.GetBatchAsync(Model, new() { (AllPagesKey0, newer, FileSize) });

        Assert.Null(results[AllPagesKey0]);
    }

    [Fact]
    public async Task GetBatchAsync_StaleWhenFileSizeChanged_ReturnsNull()
    {
        var store = CreateStore();
        await store.SaveAsync(Model, AllPagesKey0, LastModified, FileSize, MakeVector());

        var results = await store.GetBatchAsync(Model,
            new() { (AllPagesKey0, LastModified, FileSize + 1) });

        Assert.Null(results[AllPagesKey0]);
    }

    /// <summary>
    /// The reported production scenario: process 1 writes (search #1), process 2
    /// starts later and must read the same values back from the same database file.
    /// </summary>
    [Fact]
    public async Task Save_Persists_AcrossStoreInstances_SimulatesNextAppLaunch()
    {
        var vector = MakeVector();

        // "First launch": save, then shut down (disposes the connection pool).
        using (var firstLaunch = CreateStore())
        {
            await firstLaunch.SaveAsync(Model, AllPagesKey0, LastModified, FileSize, vector);
            await firstLaunch.SaveAsync(Model, CoversKey, LastModified, FileSize, vector);
        }

        // "Next launch": a brand-new store over the same file.
        using var nextLaunch = CreateStore();

        var results = await nextLaunch.GetBatchAsync(Model, new()
        {
            (AllPagesKey0, LastModified, FileSize),
            (CoversKey, LastModified, FileSize)
        });

        Assert.NotNull(results[AllPagesKey0]);
        Assert.Equal(vector, results[AllPagesKey0]);
        Assert.NotNull(results[CoversKey]);
        Assert.Equal(vector, results[CoversKey]);
    }

    /// <summary>
    /// Contract pin for the LastModified formatting: the service always passes
    /// FileInfo.LastWriteTimeUtc (Kind=Utc) to both SaveAsync and GetBatchAsync,
    /// and the store compares the round-trip ("O") strings. Values written with a
    /// Utc-kind timestamp must read back with the same Utc-kind timestamp.
    /// </summary>
    [Fact]
    public async Task RoundTrip_UtcKindTimestamp_MatchesOnRead()
    {
        var store = CreateStore();

        var utcStamp = DateTime.SpecifyKind(new DateTime(2025, 12, 23, 8, 49, 55), DateTimeKind.Utc);
        await store.SaveAsync(Model, AllPagesKey0, utcStamp, FileSize, MakeVector());

        var results = await store.GetBatchAsync(Model, new() { (AllPagesKey0, utcStamp, FileSize) });

        Assert.NotNull(results[AllPagesKey0]);
    }

    [Fact]
    public async Task Save_OverwritesPreviousValue_InsertOrReplace()
    {
        var store = CreateStore();
        var first = MakeVector(16);
        var second = MakeVector(16);
        for (int i = 0; i < second.Length; i++) second[i] += 1f;

        await store.SaveAsync(Model, AllPagesKey0, LastModified, FileSize, first);
        await store.SaveAsync(Model, AllPagesKey0, LastModified, FileSize, second);

        var readBack = await store.GetAsync(Model, AllPagesKey0, LastModified, FileSize);

        Assert.Equal(second, readBack);
    }

    /// <summary>
    /// Regression guard for the SQLite host-parameter limit (32766 since 3.32; 999
    /// before): a probe of MORE entries than the limit must not throw "too many SQL
    /// variables". Such a probe is impossible as a single IN (...) list — the store
    /// loads the probe set into a temp table and runs one join instead, so 33k+ entries
    /// must come back with the exact same hit / stale / missing classification as small
    /// probes.
    /// </summary>
    [Fact]
    public async Task GetBatchAsync_MoreEntriesThanSqliteVariableLimit_ClassifiesEveryEntry()
    {
        const int total = 33_000; // > 32766, SQLite's modern host-parameter limit

        using var store = CreateStore(); // Initialize() creates the tables in _dbPath

        // Seed the cache: one 4-dim vector row per path (the vector is shared, so the
        // blob is a single bound parameter reused across every row of each INSERT).
        // Paths where i % 100 == 50 are deliberately left UNseeded → "missing".
        var vector = MakeVector(4);
        var blob = new byte[4 + 4 * vector.Length];
        BitConverter.TryWriteBytes(blob.AsSpan(), vector.Length); // dim, LE on the target platform
        for (var i = 0; i < vector.Length; i++)
            BitConverter.TryWriteBytes(blob.AsSpan(4 + i * 4), vector[i]);

        using (var raw = new SqliteConnection($"Data Source={_dbPath}"))
        {
            raw.Open();
            using var tx = raw.BeginTransaction();
            using var cmd = raw.CreateCommand();
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("@mn", Model);
            cmd.Parameters.AddWithValue("@lm", LastModified.ToString("O"));
            cmd.Parameters.AddWithValue("@fs", FileSize);
            cmd.Parameters.AddWithValue("@emb", blob);
            cmd.Parameters.AddWithValue("@ca", LastModified.ToString("O"));

            const int rowsPerStatement = 500; // 5 shared variables per statement — no limit risk
            for (var start = 0; start < total; start += rowsPerStatement)
            {
                var parts = new List<string>();
                for (var i = 0; i < rowsPerStatement && start + i < total; i++)
                {
                    var idx = start + i;
                    if (idx % 100 == 50)
                        continue; // unseeded on purpose
                    parts.Add($"(@mn, 't/p{idx}.jpg', @lm, @fs, @emb, @ca)");
                }
                if (parts.Count == 0)
                    continue;

                cmd.CommandText = "INSERT OR IGNORE INTO embeddings " +
                    "(model_name, file_path, last_modified, file_size, embedding, created_at) " +
                    $"VALUES {string.Join(",", parts)}";
                await cmd.ExecuteNonQueryAsync();
            }
            tx.Commit();
        }

        // Probe MORE paths than SQLite allows variables for. Where i % 100 == 75 the
        // file is "touched" after caching (newer timestamp) → must come back stale.
        var probe = new List<(string, DateTime, long)>(total);
        for (var i = 0; i < total; i++)
        {
            var lm = i % 100 == 75 ? LastModified.AddHours(1) : LastModified;
            probe.Add(($"t/p{i}.jpg", lm, FileSize));
        }

        var results = await store.GetBatchAsync(Model, probe); // would throw on a single-IN implementation

        Assert.Equal(total, results.Count);
        for (var i = 0; i < total; i++)
        {
            var value = results[$"t/p{i}.jpg"];
            if (i % 100 == 50 || i % 100 == 75)
                Assert.Null(value); // missing or stale → re-encode
            else
                Assert.Equal(vector, value); // fresh cache hit
        }
    }
}