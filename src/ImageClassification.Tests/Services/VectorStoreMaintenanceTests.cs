using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

    private sealed class RecordingProgress : IProgress<VectorCacheMaintenanceStatus>
    {
        public List<VectorCacheMaintenanceStatus> Items { get; } = new();

        public void Report(VectorCacheMaintenanceStatus value) => Items.Add(value);
    }

    [Fact]
    public async Task PruneMissingFiles_ReportsProgress_PhasesAndCounters()
    {
        var lm = new DateTime(2025, 12, 23, 8, 49, 55, DateTimeKind.Utc);
        var dir = Path.Combine(Path.GetTempPath(), $"ics_prune_prog_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.png"), "a"); // exists
            // b.png is never created — its cache entry is "missing"
            using var store = new SqliteVectorStore(_dbPath);
            await store.SaveAsync("ClipViTB32", Path.Combine(dir, "a.png"), lm, 100, Vector(1));
            await store.SaveAsync("ClipViTB32", Path.Combine(dir, "b.png"), lm, 200, Vector(2));

            var progress = new RecordingProgress();
            var result = await store.PruneMissingFilesAsync(null, progress);

            Assert.True(result.Success, result.Detail);
            Assert.Contains("1 missing file", result.Detail);

            // Phase 1 is indeterminate; the file check is determinate and ends at Total
            Assert.StartsWith("Collecting", progress.Items[0].Phase);
            Assert.True(progress.Items[0].Indeterminate);
            var checkedFinal = progress.Items.Last(i => i.Phase.StartsWith("Checking files"));
            Assert.False(checkedFinal.Indeterminate);
            Assert.Equal(2, checkedFinal.Total);
            Assert.Equal(2, checkedFinal.Current);
            Assert.Equal(1, checkedFinal.MissingSoFar);
            Assert.All(progress.Items.Where(i => i.Phase.StartsWith("Checking files")),
                i => Assert.False(i.Indeterminate));

            // The missing entry is gone, the existing one is kept
            Assert.Null(await store.GetAsync("ClipViTB32", Path.Combine(dir, "b.png"), lm, 200));
            Assert.NotNull(await store.GetAsync("ClipViTB32", Path.Combine(dir, "a.png"), lm, 100));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task PruneMissingFiles_Cancelled_MakesNoChanges()
    {
        var lm = new DateTime(2025, 12, 23, 8, 49, 55, DateTimeKind.Utc);
        var dir = Path.Combine(Path.GetTempPath(), $"ics_prune_cancel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.png"), "a");
            using var store = new SqliteVectorStore(_dbPath);
            await store.SaveAsync("ClipViTB32", Path.Combine(dir, "a.png"), lm, 100, Vector(1));
            await store.SaveAsync("ClipViTB32", Path.Combine(dir, "b.png"), lm, 200, Vector(2));

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // already cancelled: must abort before any work
            var result = await store.PruneMissingFilesAsync(null, null, cts.Token);

            Assert.False(result.Success);
            Assert.Contains("Cancelled", result.Detail);

            // Both rows are still there — deletion only happens after the whole file check
            Assert.NotNull(await store.GetAsync("ClipViTB32", Path.Combine(dir, "a.png"), lm, 100));
            Assert.NotNull(await store.GetAsync("ClipViTB32", Path.Combine(dir, "b.png"), lm, 200));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task VacuumAsync_ReportsIndeterminateTicks_AndSucceeds()
    {
        var lm = new DateTime(2025, 12, 23, 8, 49, 55, DateTimeKind.Utc);
        using var store = new SqliteVectorStore(_dbPath);
        for (var i = 0; i < 16; i++)
            await store.SaveAsync("ClipViTB32", $@"I:\c\vp{i}.cbr", lm, 600 + i, Vector(i));

        var progress = new RecordingProgress();
        var result = await store.VacuumAsync(progress);

        Assert.True(result.Success, result.Detail);
        Assert.NotEmpty(progress.Items);
        // VACUUM has no progress hook in SQLite — every report is indeterminate
        Assert.All(progress.Items, i => Assert.True(i.Indeterminate));

        var info = await store.GetCacheInfoAsync();
        Assert.Equal(16, info.TotalEntries);
    }

    [Fact]
    public async Task VacuumAsync_CancelledBeforeStart_LeavesDatabaseIntact()
    {
        var lm = new DateTime(2025, 12, 23, 8, 49, 55, DateTimeKind.Utc);
        using var store = new SqliteVectorStore(_dbPath);
        for (var i = 0; i < 8; i++)
            await store.SaveAsync("ClipViTB32", $@"I:\c\vc{i}.cbr", lm, 700 + i, Vector(i));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await store.VacuumAsync(null, cts.Token);

        Assert.False(result.Success);
        Assert.Contains("unchanged", result.Detail);

        // The store is still fully usable
        var info = await store.GetCacheInfoAsync();
        Assert.Equal(8, info.TotalEntries);
        Assert.NotNull(await store.GetAsync("ClipViTB32", @"I:\c\vc3.cbr", lm, 703));
    }

    [Fact]
    public async Task PruneMissingFiles_DeletedDirectory_MarksWholeGroupMissing_OthersUntouched()
    {
        var lm = new DateTime(2025, 12, 23, 8, 49, 55, DateTimeKind.Utc);
        var dir = Path.Combine(Path.GetTempPath(), $"ics_prune_dir_{Guid.NewGuid():N}");
        var goneDir = Path.Combine(dir, "gone");
        var keptDir = Path.Combine(dir, "kept");
        Directory.CreateDirectory(goneDir);
        Directory.CreateDirectory(keptDir);
        try
        {
            File.WriteAllText(Path.Combine(goneDir, "x.png"), "x");
            File.WriteAllText(Path.Combine(keptDir, "y.png"), "y");
            using var store = new SqliteVectorStore(_dbPath);
            await store.SaveAsync("ClipViTB32", Path.Combine(goneDir, "x.png"), lm, 100, Vector(1));
            await store.SaveAsync("ClipViTB32", Path.Combine(keptDir, "y.png"), lm, 200, Vector(2));
            await store.SaveAsync("ClipViTB32", Path.Combine(keptDir, "z.png"), lm, 300, Vector(3));

            // The whole directory disappears (its files with it) — the directory check must
            // mark x.png missing without probing it file by file.
            File.Delete(Path.Combine(goneDir, "x.png"));
            Directory.Delete(goneDir);

            var result = await store.PruneMissingFilesAsync(null, null);

            Assert.True(result.Success, result.Detail);
            Assert.Contains("2 missing file", result.Detail);

            // The vanished directory's row and the missing-file row are gone; the existing
            // file's row is kept.
            Assert.Null(await store.GetAsync("ClipViTB32", Path.Combine(goneDir, "x.png"), lm, 100));
            Assert.Null(await store.GetAsync("ClipViTB32", Path.Combine(keptDir, "z.png"), lm, 300));
            Assert.NotNull(await store.GetAsync("ClipViTB32", Path.Combine(keptDir, "y.png"), lm, 200));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task PruneMissingFiles_CaseInsensitiveNameMatch_KeepsExistingFile()
    {
        var lm = new DateTime(2025, 12, 23, 8, 49, 55, DateTimeKind.Utc);
        var dir = Path.Combine(Path.GetTempPath(), $"ics_prune_case_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // The file exists on disk with different casing than the cache key (NTFS is
            // case-insensitive) — the directory comparison must keep the entry.
            File.WriteAllText(Path.Combine(dir, "Photo.PNG"), "p");
            using var store = new SqliteVectorStore(_dbPath);
            await store.SaveAsync("ClipViTB32", Path.Combine(dir, "photo.png"), lm, 100, Vector(1));

            var result = await store.PruneMissingFilesAsync(null, null);

            Assert.True(result.Success, result.Detail);
            Assert.Contains("0 missing file", result.Detail);
            Assert.NotNull(await store.GetAsync("ClipViTB32", Path.Combine(dir, "photo.png"), lm, 100));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task PruneMissingFiles_AllPhasesRunOffTheCallerThread_EvenWithASyncContextInstalled()
    {
        // Regression test for the "maintenance froze the window" bug: with a
        // SynchronizationContext installed on the caller (exactly what WPF does on its UI
        // thread), every phase of the prune — collection, file check and deletion — must
        // run on thread-pool threads. The trap is that Task.Delay(0) is pre-completed and
        // Microsoft.Data.Sqlite "async" calls run on the calling thread, so only a real
        // Task.Run hop + ConfigureAwait(false) on every continuation keeps the caller free.
        var lm = new DateTime(2025, 12, 23, 8, 49, 55, DateTimeKind.Utc);
        var dir = Path.Combine(Path.GetTempPath(), $"ics_prune_thread_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var store = new SqliteVectorStore(_dbPath);

            // One existing + one to-be-deleted file so the prune exercises the deletion
            // phase with real work.
            var keepFile = Path.Combine(dir, "keep.cbz");
            File.WriteAllText(keepFile, "x");
            var goneFile = Path.Combine(dir, "gone.cbz");
            File.WriteAllText(goneFile, "x");
            await store.SaveAsync("ClipViTB32", keepFile, lm, 1, Vector(1));
            await store.SaveAsync("ClipViTB32", goneFile, lm, 1, Vector(2));
            File.Delete(goneFile);

            var ctx = new QueueingSyncContext();
            var original = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(ctx);
            var callerThreadId = Environment.CurrentManagedThreadId;
            var recorder = new ThreadRecordingProgress();
            Task<VectorCacheMaintenanceResult> task;
            try
            {
                // The method body starts on THIS thread with the context installed —
                // exactly like the WPF dispatcher thread.
                task = store.PruneMissingFilesAsync(null, recorder, default);
            }
            finally
            {
                // Restore immediately: from here on the test itself must run under the
                // original context (post-prune GetAsync awaits would otherwise post to
                // the queue nobody pumps and hang the run).
                SynchronizationContext.SetSynchronizationContext(original);
            }

            // WPF keeps pumping the message loop while the operation is in flight;
            // emulate that until the operation completes.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (!task.IsCompleted)
            {
                if (cts.IsCancellationRequested)
                    throw new TimeoutException("Prune did not complete while pumping the caller context");
                ctx.Drain();
                await Task.Delay(5);
            }

            var result = await task;
            Assert.True(result.Success, result.Detail);
            Assert.NotNull(await store.GetAsync("ClipViTB32", keepFile, lm, 1));
            Assert.Null(await store.GetAsync("ClipViTB32", goneFile, lm, 1));

            // Every progress report (all phases, deletion included) must have been
            // produced on a thread other than the caller's.
            var reports = recorder.Reports;
            Assert.NotEmpty(reports);
            Assert.DoesNotContain(reports, r => r.ThreadId == callerThreadId);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Minimal SynchronizationContext double: queued callbacks are only executed when the
    /// test pumps <see cref="Drain"/> — like a WPF message loop.
    /// </summary>
    private sealed class QueueingSyncContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<Action> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue(() => d(state));
        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void Drain()
        {
            while (_queue.TryDequeue(out var action))
                action();
        }
    }

    /// <summary>IProgress double that records the thread each report was made on.</summary>
    private sealed class ThreadRecordingProgress : IProgress<VectorCacheMaintenanceStatus>
    {
        private readonly object _lock = new();
        private readonly List<(string Phase, int ThreadId)> _reports = new();

        public List<(string Phase, int ThreadId)> Reports
        {
            get { lock (_lock) return _reports.ToList(); }
        }

        public void Report(VectorCacheMaintenanceStatus value)
        {
            lock (_lock) _reports.Add((value.Phase, Environment.CurrentManagedThreadId));
        }
    }
}