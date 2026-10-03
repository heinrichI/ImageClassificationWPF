using ImageClassification.VectorStore;
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
}