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
    public async Task GetCacheInfo_ReflectsCoversAndArchiveRows()
    {
        var lm = new DateTime(2025, 12, 23, 8, 49, 55, DateTimeKind.Utc);
        using var store = new SqliteVectorStore(_dbPath);

        // 2 cover rows + 1 archive row holding 3 pages (counted as ONE entry)
        await store.SaveCoverAsync("ClipViTB32", @"I:\c\One.cbr", lm, 111, Vector(1));
        await store.SaveCoverAsync("ClipViTB32", @"I:\c\Two.cbr", lm, 222, Vector(2));
        await store.SaveArchiveAsync("ClipViTB32", @"I:\c\Big.cbr", 3, lm, 333,
            new[] { Vector(10), Vector(11), Vector(12) });

        var info = await store.GetCacheInfoAsync();

        Assert.Equal(3, info.TotalEntries);
        Assert.Equal(2, info.CoversEntries);
        Assert.Equal(1, info.AllPagesEntries);
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

    [Fact]
    public async Task Prune_RemovesMissingFiles_KeepsExistingAndOfflineDiskEntries()
    {
        var lm = new DateTime(2025, 12, 23, 8, 49, 55, DateTimeKind.Utc);
        var dir = Path.Combine(Path.GetTempPath(), $"ics_prune_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var store = new SqliteVectorStore(_dbPath);

            // Existing file on a connected disk → kept
            var realFile = Path.Combine(dir, "real.cbz");
            File.WriteAllText(realFile, "x");
            // Missing file on a connected disk → removed (cover row + archive row)
            var missingFile = Path.Combine(dir, "missing.cbz");
            // File on a disk that is not connected → kept (removable media may come back)
            var offlineRoot = FindOfflineDriveRoot();
            var offlineFile = offlineRoot is not null ? offlineRoot + "comic.cbz" : null;

            await store.SaveCoverAsync("ClipViTB32", realFile, lm, 1, Vector(1));
            await store.SaveCoverAsync("ClipViTB32", missingFile, lm, 1, Vector(2));
            await store.SaveArchiveAsync("ClipViTB32", missingFile, 2, lm, 1,
                new[] { Vector(3), Vector(4) });
            if (offlineFile is not null)
                await store.SaveCoverAsync("ClipViTB32", offlineFile, lm, 1, Vector(5));

            var result = await store.PruneMissingFilesAsync();

            Assert.True(result.Success, result.Detail);
            Assert.Equal("prune", result.Operation);

            Assert.NotNull(await store.GetCoverAsync("ClipViTB32", realFile, lm, 1));
            Assert.Null(await store.GetCoverAsync("ClipViTB32", missingFile, lm, 1));
            Assert.Null(await store.GetArchiveAsync("ClipViTB32", missingFile, lm, 1));

            if (offlineFile is not null)
            {
                // Disk not connected → entry must survive the prune
                Assert.NotNull(await store.GetCoverAsync("ClipViTB32", offlineFile, lm, 1));
                Assert.Contains(offlineRoot!, result.Detail);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Prune_ModelFilter_RemovesOnlyEntriesOfThatModel()
    {
        var lm = new DateTime(2025, 12, 23, 8, 49, 55, DateTimeKind.Utc);
        var dir = Path.Combine(Path.GetTempPath(), $"ics_prune_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var missingFile = Path.Combine(dir, "missing.cbz"); // never created
        try
        {
            using var store = new SqliteVectorStore(_dbPath);

            await store.SaveAsync("ClipViTB32", missingFile, lm, 1, Vector(1));
            await store.SaveAsync("OtherModel", missingFile, lm, 1, Vector(2));

            var result = await store.PruneMissingFilesAsync("OtherModel");

            Assert.True(result.Success, result.Detail);
            Assert.Equal("prune", result.Operation);

            // Other model's entry for the same missing file must be untouched
            Assert.NotNull(await store.GetAsync("ClipViTB32", missingFile, lm, 1));
            Assert.Null(await store.GetAsync("OtherModel", missingFile, lm, 1));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void BaseFilePath_StripsPageSuffix_CoversKeysUntouched()
    {
        Assert.Equal(@"I:\c\Big.cbr", SqliteVectorStore.BaseFilePath(@"I:\c\Big.cbr|page=42"));
        Assert.Equal(@"I:\c\One.cbr", SqliteVectorStore.BaseFilePath(@"I:\c\One.cbr"));
    }

    /// <summary>
    /// Finds a drive letter that is not currently ready, so tests can simulate files on
    /// disconnected removable media. Returns null when every letter is a ready drive.
    /// </summary>
    private static string? FindOfflineDriveRoot()
    {
        for (char c = 'Z'; c >= 'A'; c--)
        {
            var root = $"{c}:\\";
            try
            {
                if (!new DriveInfo(root).IsReady)
                    return root;
            }
            catch { /* keep looking */ }
        }
        return null;
    }
}