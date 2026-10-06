using ImageClassification.Core.Models;
using ImageClassification.VectorStore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ImageClassification.Tests.Services;

/// <summary>
/// Tests for the packed comic-search cache: one row per archive in
/// <c>archive_embeddings</c> (whole page array, the row doubles as the
/// "fully cached" marker) and one row per archive in
/// <c>cover_embeddings</c>. Also covers the one-time migration from the
/// legacy per-page <c>embeddings</c> rows + <c>archive_cache_markers</c>.
/// </summary>
public sealed class VectorStoreArchiveEmbeddingTests : IDisposable
{
    private const string Model = "ClipViTB32";
    private const string Archive = @"I:\DVD_Комикс_Арчи\test_archive.cbz";
    private const string Archive2 = @"J:\other\second.cbr";
    // Differs from Archive only at positions of the '_' LIKE wildcards (DVD_, test_archive)
    private const string ArchiveLookalike = @"I:\DVDxКомиксxАрчи\testXarchive.cbz";
    private const long Size = 123_456;

    private static readonly DateTime Modified = new(2025, 12, 23, 8, 49, 55, DateTimeKind.Utc);

    private readonly string _dbPath;

    public VectorStoreArchiveEmbeddingTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ics_archive_{Guid.NewGuid():N}.db");
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

    private static float[] Vector(float seed)
    {
        var v = new float[8];
        for (var i = 0; i < v.Length; i++)
            v[i] = seed + i;
        return v;
    }

    [Fact]
    public async Task SaveArchive_Then_Get_ReturnsWholePageArray()
    {
        using var store = CreateStore();
        var pages = new[] { Vector(1), Vector(2), Vector(3) };

        await store.SaveArchiveAsync(Model, Archive, 3, Modified, Size, pages);

        var hit = await store.GetArchiveAsync(Model, Archive, Modified, Size);
        Assert.NotNull(hit);
        var (pageCount, got) = hit!.Value;
        Assert.Equal(3, pageCount);
        Assert.Equal(3, got.Length);
        for (var i = 0; i < 3; i++)
            Assert.Equal(pages[i], got[i]);
    }

    [Fact]
    public async Task GetArchive_StaleWhenLastModifiedChanged_ReturnsNull()
    {
        using var store = CreateStore();
        await store.SaveArchiveAsync(Model, Archive, 1, Modified, Size, new[] { Vector(1) });

        var miss = await store.GetArchiveAsync(Model, Archive, Modified.AddHours(1), Size);
        Assert.Null(miss);
    }

    [Fact]
    public async Task GetArchive_StaleWhenFileSizeChanged_ReturnsNull()
    {
        using var store = CreateStore();
        await store.SaveArchiveAsync(Model, Archive, 1, Modified, Size, new[] { Vector(1) });

        var miss = await store.GetArchiveAsync(Model, Archive, Modified, Size + 1);
        Assert.Null(miss);
    }

    [Fact]
    public async Task SaveArchive_OverwritesPrevious_RowIsUpgradedInPlace()
    {
        using var store = CreateStore();
        await store.SaveArchiveAsync(Model, Archive, 2, Modified, Size, new[] { Vector(1), Vector(2) });

        // Archive re-downloaded with one page more → the single row is replaced
        await store.SaveArchiveAsync(Model, Archive, 3, Modified, Size, new[] { Vector(1), Vector(2), Vector(3) });

        var hit = await store.GetArchiveAsync(Model, Archive, Modified, Size);
        Assert.NotNull(hit);
        Assert.Equal(3, hit!.Value.PageCount);
        Assert.Equal(Vector(3), hit.Value.Pages[2]);

        // Still exactly one row for the archive
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM archive_embeddings WHERE model_name = $m AND archive_path = $p";
        cmd.Parameters.AddWithValue("$m", Model);
        cmd.Parameters.AddWithValue("$p", Archive);
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task GetArchive_PackedPageCountMismatch_ReturnsNull()
    {
        using (var seeded = CreateStore())
        {
            await seeded.SaveArchiveAsync(Model, Archive, 2, Modified, Size, new[] { Vector(1), Vector(2) });
        }

        // Corrupt the marker part (page_count) via raw SQL → the defensive guard treats it as a miss
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE archive_embeddings SET page_count = 99";
            cmd.ExecuteNonQuery();
        }

        using var store = CreateStore();
        var miss = await store.GetArchiveAsync(Model, Archive, Modified, Size);
        Assert.Null(miss);
    }

    [Fact]
    public async Task CoverRow_Then_Get_RoundTrips_WithStaleness()
    {
        using var store = CreateStore();
        await store.SaveCoverAsync(Model, Archive, Modified, Size, Vector(7));

        var hit = await store.GetCoverAsync(Model, Archive, Modified, Size);
        Assert.NotNull(hit);
        Assert.Equal(Vector(7), hit);

        Assert.Null(await store.GetCoverAsync(Model, Archive, Modified, Size + 1));
        Assert.Null(await store.GetCoverAsync(Model, Archive, Modified.AddMinutes(5), Size));
    }

    [Fact]
    public async Task ArchiveRowDoesNotSatisfyCoverQueries()
    {
        // Covers and all-pages caches are separate: a fresh archive row must NOT
        // masquerade as a cached cover (and vice versa).
        using var store = CreateStore();
        await store.SaveArchiveAsync(Model, Archive, 2, Modified, Size, new[] { Vector(1), Vector(2) });

        Assert.Null(await store.GetCoverAsync(Model, Archive, Modified, Size));
        Assert.NotNull(await store.GetArchiveAsync(Model, Archive, Modified, Size));
    }

    [Fact]
    public async Task ModelScoping_ArchiveAndCoverRows()
    {
        using var store = CreateStore();
        await store.SaveArchiveAsync(Model, Archive, 1, Modified, Size, new[] { Vector(1) });
        await store.SaveCoverAsync(Model, Archive, Modified, Size, Vector(1));

        Assert.Null(await store.GetArchiveAsync("OtherModel", Archive, Modified, Size));
        Assert.Null(await store.GetCoverAsync("OtherModel", Archive, Modified, Size));
        Assert.NotNull(await store.GetArchiveAsync(Model, Archive, Modified, Size));
    }

    [Fact]
    public async Task ClearModel_RemovesOnlyThatModel_ArchiveAndCoverRows()
    {
        using var store = CreateStore();
        await store.SaveArchiveAsync(Model, Archive, 1, Modified, Size, new[] { Vector(1) });
        await store.SaveCoverAsync(Model, Archive, Modified, Size, Vector(1));
        await store.SaveArchiveAsync("OtherModel", Archive, 1, Modified, Size, new[] { Vector(9) });

        await store.ClearAsync(Model);

        Assert.Null(await store.GetArchiveAsync(Model, Archive, Modified, Size));
        Assert.Null(await store.GetCoverAsync(Model, Archive, Modified, Size));
        Assert.NotNull(await store.GetArchiveAsync("OtherModel", Archive, Modified, Size));
    }

    [Fact]
    public async Task ClearAll_RemovesEverything_IncludingGenericTable()
    {
        using var store = CreateStore();
        await store.SaveArchiveAsync(Model, Archive, 1, Modified, Size, new[] { Vector(1) });
        await store.SaveCoverAsync(Model, Archive, Modified, Size, Vector(1));
        await store.SaveAsync("MobileNetV2", @"K:\loose\img.jpg", Modified, 5, Vector(1));

        await store.ClearAsync();

        Assert.Null(await store.GetArchiveAsync(Model, Archive, Modified, Size));
        Assert.Null(await store.GetCoverAsync(Model, Archive, Modified, Size));
        Assert.Null(await store.GetAsync("MobileNetV2", @"K:\loose\img.jpg", Modified, 5));

        var info = await store.GetCacheInfoAsync();
        Assert.Equal(0, info.TotalEntries);
    }

    // ------------------------------------------------------------------
    //  Legacy migration
    // ------------------------------------------------------------------

    /// <summary>
    /// Encodes a vector in the store's on-disk format (4-byte little-endian
    /// dimension header + 4 bytes per float) so hand-built legacy rows are
    /// readable by the real store.
    /// </summary>
    private static byte[] EncodeVector(float[] v)
    {
        var blob = new byte[4 + v.Length * 4];
        Buffer.BlockCopy(BitConverter.GetBytes(v.Length), 0, blob, 0, 4);
        for (var i = 0; i < v.Length; i++)
            Buffer.BlockCopy(BitConverter.GetBytes(v[i]), 0, blob, 4 + i * 4, 4);
        return blob;
    }

    /// <summary>
    /// Builds a database in the pre-redesign layout: per-page rows in
    /// <c>embeddings</c> + an <c>archive_cache_markers</c> table.
    /// </summary>
    private void BuildLegacyDb()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "CREATE TABLE embeddings (" +
                "model_name TEXT NOT NULL, file_path TEXT NOT NULL, last_modified TEXT NOT NULL, " +
                "file_size INTEGER NOT NULL, created_at TEXT NOT NULL, embedding BLOB NOT NULL, " +
                "PRIMARY KEY (model_name, file_path));" +
                "CREATE TABLE archive_cache_markers (" +
                "model_name TEXT NOT NULL, archive_path TEXT NOT NULL, page_count INTEGER NOT NULL, " +
                "last_modified INTEGER NOT NULL, file_size INTEGER NOT NULL, created_at TEXT NOT NULL, " +
                "PRIMARY KEY (model_name, archive_path));";
            cmd.ExecuteNonQuery();
        }

        string O(DateTime dt) => dt.ToString("O");
        var now = DateTime.UtcNow.ToString("O");

        // Archive 1: complete page set 0..2 + a marker (page_count=3) → must pack into one row
        for (var p = 0; p < 3; p++)
            InsertLegacyRow(conn, Model, $@"{Archive}|page={p}", O(Modified), Size, EncodeVector(Vector(p + 1)));
        InsertMarker(conn, Model, Archive, 3, Modified, Size);

        // Look-alike archive: one page, no marker. An unescaped LIKE on Archive's path would
        // match this row and disqualify Archive's packed row → regression guard for the
        // migration's wildcard escaping.
        InsertLegacyRow(conn, Model, $@"{ArchiveLookalike}|page=5", O(Modified), Size, EncodeVector(Vector(90)));

        // Cover row: bare archive path
        InsertLegacyRow(conn, Model, Archive2, O(Modified), 999, EncodeVector(Vector(50)));

        // Archive 2: partial pages WITHOUT a marker (crashed search) → must be discarded
        InsertLegacyRow(conn, Model, $@"{Archive2}|page=0", O(Modified), 999, EncodeVector(Vector(60)));
        InsertLegacyRow(conn, Model, $@"{Archive2}|page=1", O(Modified), 999, EncodeVector(Vector(61)));

        // Non-comic rows: loose image (same model) + classifier cache → must survive untouched
        InsertLegacyRow(conn, Model, @"K:\loose\img.jpg", O(Modified), 5, EncodeVector(Vector(70)));
        InsertLegacyRow(conn, "MobileNetV2", @"K:\loose\img.jpg", O(Modified), 5, EncodeVector(Vector(80)));

        tx.Commit();
    }

    private static void InsertLegacyRow(SqliteConnection conn, string model, string path, string lastModified,
        long fileSize, byte[] embedding)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO embeddings (model_name, file_path, last_modified, file_size, created_at, embedding) " +
            "VALUES ($m, $p, $lm, $fs, $ca, $e)";
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$lm", lastModified);
        cmd.Parameters.AddWithValue("$fs", fileSize);
        cmd.Parameters.AddWithValue("$ca", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$e", embedding);
        cmd.ExecuteNonQuery();
    }

    private static void InsertMarker(SqliteConnection conn, string model, string archive, int pageCount,
        DateTime lastModified, long fileSize)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO archive_cache_markers (model_name, archive_path, page_count, last_modified, file_size, created_at) " +
            "VALUES ($m, $p, $pc, $lm, $fs, $ca)";
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$p", archive);
        cmd.Parameters.AddWithValue("$pc", pageCount);
        cmd.Parameters.AddWithValue("$lm", lastModified.Ticks);
        cmd.Parameters.AddWithValue("$fs", fileSize);
        cmd.Parameters.AddWithValue("$ca", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static long CountRows(string dbPath, string table, string where = null, params object[] args)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}" + (where is null ? "" : " WHERE " + where);
        for (var i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue($"${i}", args[i]);
        return (long)cmd.ExecuteScalar()!;
    }

    private static bool TableExists(string dbPath, string table)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $t";
        cmd.Parameters.AddWithValue("$t", table);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    [Fact]
    public async Task LegacyDb_MigratesToArchiveAndCoverRows()
    {
        BuildLegacyDb();

        // Opening the store triggers the one-time migration
        using var store = CreateStore();

        // Complete archive → one packed row, readable as a hit
        var hit = await store.GetArchiveAsync(Model, Archive, Modified, Size);
        Assert.NotNull(hit);
        Assert.Equal(3, hit!.Value.PageCount);
        Assert.Equal(Vector(1), hit.Value.Pages[0]);
        Assert.Equal(Vector(3), hit.Value.Pages[2]);

        // Bare-path row → cover row
        var cover = await store.GetCoverAsync(Model, Archive2, Modified, 999);
        Assert.NotNull(cover);
        Assert.Equal(Vector(50), cover);

        // Partial pages without a marker → discarded, no archive row
        Assert.Null(await store.GetArchiveAsync(Model, Archive2, Modified, 999));

        // Look-alike archive: no row anywhere, its legacy page row removed
        Assert.Null(await store.GetArchiveAsync(Model, ArchiveLookalike, Modified, Size));
        Assert.Null(await store.GetAsync(Model, $@"{ArchiveLookalike}|page=5", Modified, Size));

        // Legacy comic rows are gone from the generic table; other rows survived
        Assert.Equal(0, CountRows(_dbPath, "embeddings",
            "model_name = $0 AND (file_path LIKE '%|page=%' OR file_path = $1)", Model, Archive));
        Assert.Null(await store.GetAsync(Model, $@"{Archive}|page=0", Modified, Size));
        Assert.NotNull(await store.GetAsync(Model, @"K:\loose\img.jpg", Modified, 5));
        Assert.NotNull(await store.GetAsync("MobileNetV2", @"K:\loose\img.jpg", Modified, 5));

        // The markers table is dropped
        Assert.False(TableExists(_dbPath, "archive_cache_markers"));
        Assert.True(TableExists(_dbPath, "archive_embeddings"));
        Assert.True(TableExists(_dbPath, "cover_embeddings"));
    }

    [Fact]
    public async Task LegacyDb_MigrationIsIdempotentOnSecondOpen()
    {
        BuildLegacyDb();
        (int PageCount, float[][] Pages)? first = null;
        using (var storeFirst = CreateStore())
        {
            // First open migrates
            first = await storeFirst.GetArchiveAsync(Model, Archive, Modified, Size);
            Assert.NotNull(first);
        }

        // Second open: no legacy rows remain → migration is a no-op, data unchanged
        using var store = CreateStore();
        var second = await store.GetArchiveAsync(Model, Archive, Modified, Size);
        Assert.NotNull(second);
        Assert.Equal(first!.Value.PageCount, second.Value.PageCount);
        Assert.Equal(1, CountRows(_dbPath, "archive_embeddings"));
    }

    [Fact]
    public async Task FreshDb_WithoutLegacyRows_MigrationIsNoop()
    {
        using var store = CreateStore();
        await store.SaveArchiveAsync(Model, Archive, 2, Modified, Size, new[] { Vector(1), Vector(2) });
        Assert.Null(await store.GetAsync(Model, Archive, Modified, Size));
        Assert.NotNull(await store.GetArchiveAsync(Model, Archive, Modified, Size));
    }
}