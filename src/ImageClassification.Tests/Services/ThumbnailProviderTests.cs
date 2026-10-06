using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
using ImageClassification.Tests;
using ImageClassification.UI.Configuration;
using ImageClassification.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ImageClassification.Tests.Services;

/// <summary>
/// ThumbnailProvider queue/cache semantics: LIFO work order (newest request first),
/// width-aware cache keys, full Clear (cache + queue) and the shared frozen placeholder.
/// </summary>
public class ThumbnailProviderTests : IDisposable
{
    public void Dispose() { }
    /// <summary>Valid 1x1 RGBA PNG.</summary>
    private static readonly byte[] Png1x1 =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x62, 0x60, 0x00, 0x01, 0x00,
        0x00, 0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4,
        0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44,
        0xAE, 0x42, 0x60, 0x82
    };

    /// <summary>
    /// Fake cover service whose extraction of each archive can be gated by the test
    /// (a TCS per path), so push order into the decode queue is deterministic.
    /// </summary>
    private sealed class GateCoverService : IComicCoverSearchService
    {
        private readonly Dictionary<string, TaskCompletionSource<byte[]?>> _gates = new();
        private readonly Dictionary<string, byte[]> _data = new();

        public void Arm(string path, byte[] data)
        {
            _data[path] = data;
            _gates[path] = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task<byte[]?> ExtractPageAsync(string archivePath, int pageIndex) => _gates[archivePath].Task;

        public void Complete(string path) => _gates[path].SetResult(_data[path]);

        public Task LoadModelAsync(string clipOnnxPath) => Task.CompletedTask;

        public Task<List<ComicCoverResult>> SearchAsync(string directoryPath, string query,
            IProgress<ComicSearchStatus>? status = null, CancellationToken ct = default)
            => Task.FromResult(new List<ComicCoverResult>());

        public Task<List<ComicCoverResult>> SearchAllPagesAsync(string directoryPath, string query,
            IProgress<ComicSearchStatus>? status = null, CancellationToken ct = default)
            => Task.FromResult(new List<ComicCoverResult>());

        public Task ClearCacheAsync() => Task.CompletedTask;

        public void Dispose() { }
    }

    [Fact]
    public async Task LifoQueue_ServesNewestRequestFirst()
    {
        // Three real files; the queue is fed in call order c, b, a (a pushed last =
        // the newest request). The LIFO worker must decode in reverse: a, b, c.
        // Enqueue pushes synchronously on the test thread with no work between the
        // three calls, so all items are in the stack before the worker's first drain
        // and the asserted order is exactly the LIFO property (residual race is a
        // ~1 µs preemption window between the three lock-protected pushes).
        var svc = new GateCoverService();
        using var provider = CreateProvider(svc);

        var files = new[] { WriteTempPng(), WriteTempPng(), WriteTempPng() };
        var (c, b, a) = (files[0], files[1], files[2]);

        var order = new List<string>();
        var orderLock = new object();
        // A missed file reports null; a successful decode reports a BitmapImage.
        void Cb(string tag, BitmapSource? image)
        {
            if (image is not BitmapImage) return;
            lock (orderLock) order.Add(tag);
        }

        try
        {
            provider.Enqueue(c, 32, img => Cb("c", img));
            provider.Enqueue(b, 32, img => Cb("b", img));
            provider.Enqueue(a, 32, img => Cb("a", img));

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                int count;
                lock (orderLock) count = order.Count;
                if (count >= 3 || DateTime.UtcNow > deadline) break;
                await Task.Delay(10);
            }

            lock (orderLock)
                Assert.Equal(new[] { "a", "b", "c" }, order);
        }
        finally
        {
            foreach (var f in files)
            {
                try { File.Delete(f); } catch { /* best effort */ }
            }
        }
    }

    private static string WriteTempPng()
    {
        var file = Path.Combine(Path.GetTempPath(), $"ics_thumb_lifo_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(file, Png1x1);
        return file;
    }

    [Fact]
    public async Task WidthIsPartOfCacheKey_DifferentWidthsDecodeSeparately()
    {
        var file = Path.Combine(Path.GetTempPath(), $"ics_thumb_width_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(file, Png1x1);

        var svc = new GateCoverService();
        using var provider = CreateProvider(svc);

        var results64 = new List<BitmapSource?>();
        var results128 = new List<BitmapSource?>();
        var lock64 = new object();
        var lock128 = new object();

        try
        {
            // Each GetBitmap reports the placeholder immediately; a miss then enqueues a
            // decode whose completion reports the real bitmap (second callback).
            provider.GetBitmap(file, 64, b => { lock (lock64) results64.Add(b); });
            provider.GetBitmap(file, 128, b => { lock (lock128) results128.Add(b); });

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                int c64, c128;
                lock (lock64) c64 = results64.Count;
                lock (lock128) c128 = results128.Count;
                if (c64 >= 2 && c128 >= 2 || DateTime.UtcNow > deadline) break;
                await Task.Delay(10);
            }

            // The 128-wide request must have been a cache MISS (width is part of the key):
            // it got its own placeholder + its own decoded bitmap. Without the width in
            // the key it would hit the 64-wide entry and its decode callback never fired.
            lock (lock64) Assert.True(results64.Count >= 2, $"64-wide callbacks: {results64.Count}");
            lock (lock128) Assert.True(results128.Count >= 2, $"128-wide callbacks: {results128.Count}");

            lock (lock128)
                Assert.All(results128.Where(b => b is not null), b => Assert.Equal(128, b!.PixelWidth));
        }
        finally
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Clear_DiscardsPendingQueueItems_AndCache()
    {
        // "slow" decodes a huge image (keeps the worker busy), so b and c pile up in
        // the queue behind it — exactly the state Clear() must discard.
        var slowPng = MakeNoisePng(6144, 6144);
        var svc = new GateCoverService();
        svc.Arm("slow", slowPng);
        svc.Arm("b", Png1x1);
        svc.Arm("c", Png1x1);
        using var provider = CreateProvider(svc);

        var cbSlow = new List<BitmapSource?>();
        var cbB = new List<BitmapSource?>();
        var cbC = new List<BitmapSource?>();
        var lck = new object();
        Action<BitmapSource?> To(List<BitmapSource?> l, object lck)
        {
            return image =>
            {
                lock (lck) l.Add(image);
            };
        }

        provider.EnsurePageAndEnqueue("slow", 0, To(cbSlow, lck));
        provider.EnsurePageAndEnqueue("b", 0, To(cbB, lck));
        provider.EnsurePageAndEnqueue("c", 0, To(cbC, lck));

        // Start the slow decode and pin the schedule on a STATE (not a sleep): the worker
        // must actually be decoding "slow" before b and c are released, so they accumulate
        // in the queue behind it.
        svc.Complete("slow");
        await WaitForAsync(
            () => provider.IsWorkerDecoding && provider.ActiveSource == "slow",
            30_000, "the worker to be decoding 'slow' right now");
        svc.Complete("b");
        svc.Complete("c");

        // Wait until b and c are actually waiting in the queue behind the slow decode.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (provider.QueueDepth < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(5);
        Assert.True(provider.QueueDepth >= 2, $"queue depth {provider.QueueDepth} — slow decode finished too fast");

        // Act
        provider.Clear();

        // Assert: queue is empty. The in-flight slow decode was already popped from
        // the queue, so it still completes and fires its callback.
        Assert.Equal(0, provider.QueueDepth);

        var d2 = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            int n;
            lock (lck) n = cbSlow.Count;
            if (n >= 1 || DateTime.UtcNow > d2) break;
            await Task.Delay(10);
        }

        lock (lck)
        {
            Assert.True(cbSlow.Count >= 1, "the in-flight slow decode was already popped and completes");
            Assert.Empty(cbB);
            Assert.Empty(cbC);
        }
    }

    [Fact]
    public async Task PriorityBump_VisibleTileJumpsTheExtractionQueue()
    {
        // One extraction slot; 8 items pre-queued exactly like the VM (last displayed item
        // first, item 0 last). The first pushed item (files[7]) grabs the only slot, so it
        // extracts first; the rest wait, newest (files[0]) on top of the LIFO gate. Item 3
        // then becomes visible (repeat request = bump) and must start BEFORE files[0]:
        // 7, 3, 0, 1, 2, 4, 5, 6.
        var svc = new DelayedCoverService(delayMs: 150);
        using var provider = new ThumbnailProvider(
            NullLogger<ThumbnailProvider>.Instance,
            new FakeUserSettingsStore { ThumbnailWidth = 32, ThumbnailHeight = 32 },
            svc,
            maxConcurrentExtractions: 1);

        var files = Enumerable.Range(0, 8).Select(_ => WriteTempPng()).ToArray();
        try
        {
            for (var i = 7; i >= 0; i--)
            {
                provider.EnsurePageAndEnqueue(files[i], 0, null);
            }

            // Wait for the first extraction (the first pushed item) to start.
            var d1 = DateTime.UtcNow.AddSeconds(5);
            while (svc.StartedCount < 1 && DateTime.UtcNow < d1)
            {
                await Task.Delay(5);
            }
            Assert.Equal(files[7], svc.StartOrder[0]);

            // Item 3 becomes visible — a repeat request is a priority bump.
            provider.EnsurePageAndEnqueue(files[3], 0, null);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (svc.StartedCount < 8 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.Equal(8, svc.StartedCount);
            Assert.Equal(
                new[] { files[7], files[3], files[0], files[1], files[2], files[4], files[5], files[6] },
                svc.StartOrder);
        }
        finally
        {
            foreach (var f in files)
            {
                try { File.Delete(f); } catch { /* best effort */ }
            }
        }
    }

    [Fact]
    public async Task PriorityBump_VisibleTileJumpsTheDecodeQueue()
    {
        // The worker is busy decoding a huge image; a and b pile up in the decode queue
        // behind it. Bumping a (it became visible) must move it in front of b: order
        // slow, a, b — not slow, b, a.
        var slowPng = MakeNoisePng(6144, 6144);
        var orderLock = new object();
        var attemptOrders = new List<string>();
        // ThumbnailWidth = 0 → no down-scaling: the huge noise image decodes at full size, so
        // the worker is reliably busy while a and b pile up in the queue behind it.

        // Pin the schedule on an EXACT state (not a sleep): the worker must be decoding
        // "slow" right now (test seam) before a and b are released, so the queue holds both
        // behind it when the bump happens. A sleep can't guarantee this: on a fast machine
        // the decode finishes before the bump and the test observes a valid, but different,
        // order. Unlucky interleavings are retried with a fresh pipeline.
        for (var attempt = 0; ; attempt++)
        {
            Assert.True(attempt < 8, $"unlucky interleaving eight times in a row (orders: {string.Join("; ", attemptOrders)})");

            var svcAttempt = new GateCoverService();
            svcAttempt.Arm("slow", slowPng);
            svcAttempt.Arm("a", Png1x1);
            svcAttempt.Arm("b", Png1x1);
            var providerAttempt = new ThumbnailProvider(
                NullLogger<ThumbnailProvider>.Instance,
                new FakeUserSettingsStore { ThumbnailWidth = 0 },
                svcAttempt);

            var orderAttempt = new List<string>();
            using (providerAttempt)
            {
                void CbAttempt(string tag, BitmapSource? image)
                {
                    if (image is not BitmapImage) return;
                    lock (orderLock) orderAttempt.Add(tag);
                }

                providerAttempt.EnsurePageAndEnqueue("slow", 0, img => CbAttempt("slow", img));
                providerAttempt.EnsurePageAndEnqueue("a", 0, img => CbAttempt("a", img));
                providerAttempt.EnsurePageAndEnqueue("b", 0, img => CbAttempt("b", img));

                svcAttempt.Complete("slow");
                await WaitForAsync(
                    () => providerAttempt.IsWorkerDecoding && providerAttempt.ActiveSource == "slow",
                    30_000, "the worker to be decoding 'slow' right now");
                svcAttempt.Complete("a");
                svcAttempt.Complete("b");

                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (providerAttempt.QueueDepth < 2 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(5);
                }

                // Guard: the bump only reorders the pending queue. If the worker is no longer
                // on "slow" it has already snapshotted the batch and the bump is a no-op —
                // the attempt is inconclusive, restart it.
                if (!(providerAttempt.IsWorkerDecoding && providerAttempt.ActiveSource == "slow"))
                    continue;

                // a becomes visible — a repeat request is a priority bump in the decode queue.
                providerAttempt.EnsurePageAndEnqueue("a", 0, img => CbAttempt("a", img));

                var d2 = DateTime.UtcNow.AddSeconds(10);
                while (true)
                {
                    int count;
                    lock (orderLock) count = orderAttempt.Count;
                    if (count >= 3 || DateTime.UtcNow > d2) break;
                    await Task.Delay(10);
                }
            }

            lock (orderLock)
            {
                attemptOrders.Add(string.Join(",", orderAttempt));
                if (orderAttempt.SequenceEqual(new[] { "slow", "a", "b" }))
                {
                    return; // the bump moved a in front of b — captured
                }
            }
        }
    }

    [Fact]
    public async Task BumpOfLoadedTile_IsNoOp()
    {
        // A repeat request for a tile whose thumbnail is already decoded must not
        // re-extract (no duplicate archive IO on USB).
        var svc = new DelayedCoverService(delayMs: 20);
        using var provider = new ThumbnailProvider(
            NullLogger<ThumbnailProvider>.Instance,
            new FakeUserSettingsStore { ThumbnailWidth = 32, ThumbnailHeight = 32 },
            svc,
            maxConcurrentExtractions: 1);

        var file = WriteTempPng();
        try
        {
            var loaded = 0;
            provider.EnsurePageAndEnqueue(file, 0, b => { if (b is BitmapImage) Interlocked.Increment(ref loaded); });

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref loaded) < 1 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(5);
            }
            Assert.Equal(1, svc.StartedCount);

            // The tile is visible again (scrolled back) — repeat requests, no re-extraction.
            provider.EnsurePageAndEnqueue(file, 0, null);
            provider.EnsurePageAndEnqueue(file, 0, null);
            await Task.Delay(150);
            Assert.Equal(1, svc.StartedCount);
        }
        finally
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Clear_StopsStalePipeline_AndResetsStatus()
    {
        // One slot: a slow extraction (a) is in flight, b is parked on the gate. A new
        // search (Clear) must: cancel the parked b (it never extracts), make the in-flight
        // a discard its result when it finishes, reset the status line, and leave the
        // pipeline fully working for the new search.
        var svc = new DelayedCoverService(delayMs: 300);
        using var provider = new ThumbnailProvider(
            NullLogger<ThumbnailProvider>.Instance,
            new FakeUserSettingsStore { ThumbnailWidth = 32, ThumbnailHeight = 32 },
            svc,
            maxConcurrentExtractions: 1);

        var a = WriteTempPng();
        var b = WriteTempPng();
        var c = WriteTempPng();
        try
        {
            provider.EnsurePageAndEnqueue(a, 0, null); // takes the only slot
            provider.EnsurePageAndEnqueue(b, 0, null); // parks on the gate

            var d1 = DateTime.UtcNow.AddSeconds(5);
            while (svc.StartedCount < 1 && DateTime.UtcNow < d1)
            {
                await Task.Delay(5);
            }
            Assert.Equal(1, svc.StartedCount);

            // A new search starts.
            provider.Clear();

            // b is cancelled (never extracted); a's extraction finishes and is discarded
            // as stale — no decode, no status updates.
            await Task.Delay(500);
            Assert.Equal(1, svc.StartedCount);
            Assert.Equal(string.Empty, provider.StatusText);

            // The new search works normally.
            provider.EnsurePageAndEnqueue(c, 0, null);
            var d2 = DateTime.UtcNow.AddSeconds(5);
            while (svc.StartedCount < 2 && DateTime.UtcNow < d2)
            {
                await Task.Delay(5);
            }
            Assert.Equal(2, svc.StartedCount);
            Assert.Equal(c, svc.StartOrder[1]);
        }
        finally
        {
            foreach (var f in new[] { a, b, c })
            {
                try { File.Delete(f); } catch { /* best effort */ }
            }
        }
    }

    [Fact]
    public void GetPlaceholder_ReturnsSameFrozenInstance()
    {
        var svc = new GateCoverService();
        using var provider = CreateProvider(svc);

        var p1 = provider.GetPlaceholder();
        var p2 = provider.GetPlaceholder();

        Assert.Same(p1, p2);
        Assert.True(p1.IsFrozen);
        Assert.Equal(64, p1.PixelWidth);   // FakeUserSettingsStore test default
        Assert.Equal(96, p1.PixelHeight);
    }

    private static ThumbnailProvider CreateProvider(GateCoverService svc)
    {
        var settings = new FakeUserSettingsStore { ThumbnailWidth = 64, ThumbnailHeight = 96 };
        return new ThumbnailProvider(
            NullLogger<ThumbnailProvider>.Instance,
            settings,
            svc);
    }

    /// <summary>
    /// Fake cover service with a fixed extraction delay; records the order in which
    /// extractions START (a start = the caller holds an extraction slot).
    /// </summary>
    private sealed class DelayedCoverService : IComicCoverSearchService
    {
        private readonly int _delayMs;
        private readonly object _lock = new();
        private readonly List<string> _startOrder = new();

        public DelayedCoverService(int delayMs) { _delayMs = delayMs; }

        public IReadOnlyList<string> StartOrder { get { lock (_lock) { return _startOrder.ToList(); } } }
        public int StartedCount { get { lock (_lock) { return _startOrder.Count; } } }

        public Task<byte[]?> ExtractPageAsync(string archivePath, int pageIndex)
            => Task.Run(async () =>
            {
                lock (_lock) { _startOrder.Add(archivePath); }
                await Task.Delay(_delayMs);
                return Png1x1;
            });

        public Task LoadModelAsync(string clipOnnxPath) => Task.CompletedTask;

        public Task<List<ComicCoverResult>> SearchAsync(string directoryPath, string query,
            IProgress<ComicSearchStatus>? status = null, CancellationToken ct = default)
            => Task.FromResult(new List<ComicCoverResult>());

        public Task<List<ComicCoverResult>> SearchAllPagesAsync(string directoryPath, string query,
            IProgress<ComicSearchStatus>? status = null, CancellationToken ct = default)
            => Task.FromResult(new List<ComicCoverResult>());

        public Task ClearCacheAsync() => Task.CompletedTask;

        public void Dispose() { }
    }

    [Fact]
    public async Task LifoExtractionGate_NewestWaiterGetsTheSlotFirst()
    {
        // One extraction slot, each extraction takes 150 ms. Item 0 grabs the slot at t=0;
        // items 1..7 join the wait queue (10 ms apart) well before any extraction finishes,
        // so all seven are parked when the first slot frees. The LIFO gate must hand the
        // slot to the NEWEST waiter every time: start order 0,7,6,5,4,3,2,1 (a FIFO
        // semaphore — the old implementation — produced 0,1,2,...,7, i.e. the sequential
        // top-to-bottom loading the user saw while scrolling to the bottom).
        var svc = new DelayedCoverService(delayMs: 150);
        using var provider = new ThumbnailProvider(
            NullLogger<ThumbnailProvider>.Instance,
            new FakeUserSettingsStore { ThumbnailWidth = 32, ThumbnailHeight = 32 },
            svc,
            maxConcurrentExtractions: 1);

        var files = Enumerable.Range(0, 8).Select(_ => WriteTempPng()).ToArray();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                provider.EnsurePageAndEnqueue(files[i], 0, null);
                if (i < 7)
                    await Task.Delay(10);
            }

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (svc.StartedCount < 8 && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.Equal(8, svc.StartedCount);
            Assert.Equal(
                new[] { files[0], files[7], files[6], files[5], files[4], files[3], files[2], files[1] },
                svc.StartOrder);
        }
        finally
        {
            foreach (var f in files)
            {
                try { File.Delete(f); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Encodes a gradient (compresses well) as PNG; decoding it is expensive enough to
    /// keep the single decode worker busy while other work items pile up in the queue.
    /// </summary>
    private static byte[] MakeGradientPng(int w, int h)
    {
        var stride = w * 4;
        var pixels = new byte[h * stride];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var o = y * stride + x * 4;
                pixels[o + 0] = (byte)(x * 255 / Math.Max(1, w - 1));
                pixels[o + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                pixels[o + 2] = (byte)(((x + y) / 2) % 256);
                pixels[o + 3] = 0xFF;
            }
        }

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        src.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Encodes random pixels (incompressible PNG) as the "slow decode" fixture. Unlike a gradient
    /// (which compresses to a few KB and decodes in tens of ms), a noise PNG decodes in 1-2 s
    /// because the inflate step is CPU-bound — reliably long enough to pin the decode queue behind
    /// one item on any machine.
    /// </summary>
    private static byte[] MakeNoisePng(int w, int h)
    {
        var stride = w * 4;
        var pixels = new byte[h * stride];
        new Random(42).NextBytes(pixels);

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        src.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// State-based wait (no fixed sleep): polls <paramref name="condition"/> until true or the
    /// timeout, failing with a message that names what the test was waiting for.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs, string what)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
        Assert.True(condition(), $"timed out waiting for: {what}");
    }
}