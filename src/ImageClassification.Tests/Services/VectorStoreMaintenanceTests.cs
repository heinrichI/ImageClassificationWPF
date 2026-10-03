using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImageClassification.Core.Models;
using ImageClassification.VectorStore;
using Xunit;

namespace ImageClassification.Tests.Services;

/// <summary>
/// Maintenance operations on the real SqliteVectorStore (info snapshot, WAL checkpoint, vacuum).
/// </summary>
public class VectorStoreMaintenanceTests : IDisposable
{
    private readonly string _dbPath;

    public VectorStoreMaintenanceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ics_vec_maint_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }

    private static float[] Vector(int seed)
    {
        var v = new float[512];
        for (var i = 0; i < v.Length; i++) v[i] = (float)(seed + i) / 1000f;
        return v;
    }

    [Fact]
    public async Task GetCacheInfo_EmptyStore_ReportsZeros()
    {
        using var store = new SqliteVectorStore(_dbPath);

        var info = await store.GetCacheInfoAsync();

        Assert.Equal(_dbPath, info.DatabasePath);
        Assert.Equal(0, info.TotalEntries);
        Assert.Equal(0, info.CoversEntries);
        Assert.Equal(0, info.AllPagesEntries);
        Assert.Null(info.OldestEntryUtc);
        Assert.Null(info.NewestEntryUtc);
        Assert.Equal(string.Empty, info.Models);
        Assert.True(info.DatabaseFileSizeBytes >= 0);
    }

    [Fact]
    public async Task GetCacheInfo_ReflectsCoversAndAllPagesSplits()
    {
        var lm = new DateTime(2025, 12, 23, 8, 49, 55, DateTimeKind.Utc);
        using var store = new SqliteVectorStore(_dbPath);

        // 2 covers keys + 3 all-pages keys (one archive, pages 0..2)
        await store.SaveAsync("ClipViTB32", @"I:\c\One.cbr", lm, 111, Vector(1));
        await store.SaveAsync("ClipViTB32", @"I:\c\Two.cbr", lm, 222, Vector(2));
        for (var p = 0; p < 3; p++)
            await store.SaveAsync("ClipViTB32", $@"I:\c\Big.cbr|page={p}", lm, 333, Vector(10 + p));

        var info = await store.GetCacheInfoAsync();

        Assert.Equal(5, info.TotalEntries);
        Assert.Equal(2, info.CoversEntries);
        Assert.Equal(3, info.AllPagesEntries);
        Assert.Contains("ClipViTB32", info.Models);
        Assert.NotNull(info.OldestEntryUtc);
        Assert.NotNull(info.NewestEntryUtc);
        Assert.True(info.OldestEntryUtc <= info.NewestEntryUtc);
    }

    [Fact]
    public async Task OptimizeAsync_CheckpointsWal_DataSurvives_InNewInstance()
    {
        var lm = new DateTime(2025, 12, 23, 8, 49, 55, DateTimeKind.Utc);
        using var store = new SqliteVectorStore(_dbPath);

        // Enough data for several WAL frames (each 512-float vector ~2 KB)
        for (var i = 0; i < 32; i++)
            await store.SaveAsync("ClipViTB32", $@"I:\c\a{i}.cbr|page=0", lm, 1000 + i, Vector(i));

        var before = await store.GetCacheInfoAsync();

        var result = await store.OptimizeAsync();

        Assert.True(result.Success, result.Detail);
        Assert.Equal("optimize", result.Operation);

        var after = await store.GetCacheInfoAsync();
        // TRUNCATE mode must shrink the WAL file to (near) zero
        Assert.True(after.WalFileSizeBytes <= before.WalFileSizeBytes,
            $"WAL grew after optimize: {before.WalFileSizeBytes} -> {after.WalFileSizeBytes}");

        // Close the pool (frees any residual WAL) and prove the data now lives in the main file
        store.Dispose();
        using var reopened = new SqliteVectorStore(_dbPath);
        var hit = await reopened.GetAsync("ClipViTB32", @"I:\c\a5.cbr|page=0", lm, 1005);
        Assert.NotNull(hit);
        Assert.Equal(512, hit!.Length);
    }

    [Fact]
    public async Task VacuumAsync_RebuildsDatabase_DataSurvives()
    {
        var lm = new DateTime(2025, 12, 23, 8, 49, 55, DateTimeKind.Utc);
        using var store = new SqliteVectorStore(_dbPath);

        for (var i = 0; i < 16; i++)
            await store.SaveAsync("ClipViTB32", $@"I:\c\v{i}.cbr", lm, 500 + i, Vector(i));
        // Replace a few entries to create free-space holes
        for (var i = 0; i < 8; i++)
            await store.SaveAsync("ClipViTB32", $@"I:\c\v{i}.cbr", lm, 500 + i, Vector(100 + i));

        var result = await store.VacuumAsync();

        Assert.True(result.Success, result.Detail);
        Assert.Equal("vacuum", result.Operation);

        var info = await store.GetCacheInfoAsync();
        Assert.Equal(16, info.TotalEntries);

        var hit = await store.GetAsync("ClipViTB32", @"I:\c\v3.cbr", lm, 503);
        Assert.NotNull(hit);
        // The overwritten value must be the post-replace vector
        Assert.Equal(Vector(103)[10], hit![10]);
    }
}