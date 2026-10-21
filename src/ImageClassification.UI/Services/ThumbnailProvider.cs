using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageClassification.Core.Services;
using ImageClassification.UI.Configuration;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;

namespace ImageClassification.UI.Services
{
    /// <summary>
    /// Delivery payload of the thumbnail pipeline: the (width-scaled) preview plus the
    /// ORIGINAL size of the displayed image. The original size is read from the raw source
    /// (file header, or the extracted archive-page bytes) because the scaled bitmap no longer
    /// carries it. Width/Height are 0 when the source format could not be identified;
    /// ByteSize is the size of the displayed image in bytes (file size on disk, or the
    /// extracted page size for archive pages) and 0 when unknown.
    /// </summary>
    public readonly record struct ThumbnailResult(BitmapSource? Image, int Width, int Height, long ByteSize);


    /// <summary>
    /// Thumbnail pipeline (AntiDupl parity + archive-aware extensions):
    /// extraction (slow archive IO) and decode (single worker) are both LIFO, and a repeat
    /// request for a tile already in flight (its tile just became visible) is a PRIORITY
    /// BUMP — the tile is moved to the front of whichever queue it sits in instead of being
    /// re-extracted. Combined with the UI (tiles request their preview on visibility; the
    /// search pre-queues the whole list in the background) that gives: what is on screen
    /// loads first, everything else loads while the user is looking at the current tiles.
    /// </summary>
    public class ThumbnailProvider : IDisposable
    {
        private readonly ILogger<ThumbnailProvider> _logger;
        private readonly IUserSettingsStore _settings;
        private readonly IComicCoverSearchService _coverService;

        // LRU-bounded in-memory thumbnail cache: _cacheOrder holds keys from least to
        // recently used; a cache access moves the key to the end (hot thumbnails survive
        // eviction) and entries beyond MaxCachedThumbnails are evicted from the front.
        // All access happens under _cacheLock (UI thread reads, worker thread adds).
        // Cache keys are "{source}#{width}" so a width change in the settings can never
        // serve a bitmap decoded at a different size. Each entry also stores the original
        // size of the source (width/height/byte size) so a cache hit — which never goes
        // through the decode worker — still delivers it.
        private readonly object _cacheLock = new();
        private readonly Dictionary<string, ThumbnailResult> _cache = new();
        private readonly List<string> _cacheOrder = new();

        // Lock order (never take them in the reverse direction):
        //   _cacheLock (standalone) | _tileLock > _extractGateLock | _tileLock > _queueLock
        // LIFO decode queue: a List used as a stack — index 0 is the NEWEST item, the
        // worker drains from the front, and a visibility bump re-inserts its item at index
        // 0 (a Stack would not allow reordering in place). The consumer wakes on a signal
        // (no polling), draining everything that is pending at once.
        private readonly object _queueLock = new();
        private readonly List<WorkItem> _decodeQueue = new();
        private readonly SemaphoreSlim _queueSignal = new(0, 1);
        private bool _signalOutstanding; // guarded by _queueLock: at most one pending wake-up
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _worker;

        // Per-cacheKey pipeline state: makes repeat requests idempotent (no double archive
        // IO) and turns them into queue re-ordering (priority bump). Guarded by _tileLock.
        private readonly object _tileLock = new();
        private readonly Dictionary<string, TileTask> _tiles = new();

        // Every result spawns its own Task.Run in EnsurePageAndEnqueue; without a cap a large
        // result set would open thousands of archives at once (thread-pool bloat, IO/CPU
        // contention, gigabytes of full-res page bytes accumulating in the decode queue →
        // long GC pauses). Extraction follows the SAME LIFO policy as the decode queue:
        // a SemaphoreSlim hands slots to waiters in FIFO order (top-to-bottom no matter
        // where the user scrolled), so this gate hands a freed slot to the NEWEST waiter.
        private readonly object _extractGateLock = new();
        private int _extractFreeSlots;
        private readonly int _maxExtractions;
        private readonly Stack<TaskCompletionSource<bool>> _extractWaiters = new();

        // Search generation: Clear() bumps it; in-flight work of the previous search sees
        // the bump when it finishes and discards its result (no decode, no callback, no
        // status updates) so the status line never keeps showing the old search's files.
        private int _generation;

        public ThumbnailProvider(ILogger<ThumbnailProvider> logger, IUserSettingsStore settings,
            IComicCoverSearchService coverService, int maxConcurrentExtractions = 4)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _coverService = coverService ?? throw new ArgumentNullException(nameof(coverService));
            _maxExtractions = Math.Max(1, maxConcurrentExtractions);
            _extractFreeSlots = _maxExtractions;
            _worker = Task.Run(WorkerAsync);
        }

        /// <summary>
        /// Acquires one extraction slot for a new tile, called under <c>_tileLock</c> so the
        /// push order decides the gate order: the first request takes the slot, later ones
        /// park as gate waiters (the newest waiter gets the next freed slot). Without the
        /// request-time consumption all background tasks would race for the slot.
        /// </summary>
        private void TakeSlotOrPark(TileTask tile)
        {
            lock (_extractGateLock)
            {
                if (_extractFreeSlots > 0)
                {
                    _extractFreeSlots--;
                    tile.Extracting = true;
                }
                else
                {
                    tile.GateWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _extractWaiters.Push(tile.GateWaiter);
                }
            }
        }

        /// <summary>Hands the freed slot to the newest pending waiter (or keeps it free).</summary>
        private void LifoRelease()
        {
            TaskCompletionSource<bool>? granted = null;
            lock (_extractGateLock)
            {
                // Skip waiters cancelled while queued (their TCS is already completed).
                while (_extractWaiters.Count > 0)
                {
                    var waiter = _extractWaiters.Pop();
                    if (!waiter.Task.IsCompleted)
                    {
                        granted = waiter;
                        break;
                    }
                }
                if (granted is null)
                {
                    // Clamp: slots released by the previous search's stale in-flight work
                    // after a Clear() must not grow the pool beyond the cap.
                    if (_extractFreeSlots < _maxExtractions)
                        _extractFreeSlots++;
                }
            }
            granted?.TrySetResult(true);
        }

        /// <summary>Moves the tile's gate waiter to the top of the wait stack (called under _tileLock).</summary>
        private void BumpGateWaiter(TileTask tile)
        {
            var tcs = tile.GateWaiter!;
            lock (_extractGateLock)
            {
                var below = new List<TaskCompletionSource<bool>>();
                while (_extractWaiters.Count > 0)
                {
                    var waiter = _extractWaiters.Pop();
                    if (ReferenceEquals(waiter, tcs)) break;
                    below.Add(waiter);
                }
                for (var i = below.Count - 1; i >= 0; i--)
                    _extractWaiters.Push(below[i]); // restore the relative order of the others
                _extractWaiters.Push(tcs); // bumped waiter becomes the newest
            }
        }

        /// <summary>Moves the tile's work item to the front of the decode queue (called under _tileLock).</summary>
        private void BumpDecodeQueue(TileTask tile)
        {
            var item = tile.DecodeWork!;
            lock (_queueLock)
            {
                var idx = _decodeQueue.IndexOf(item);
                if (idx > 0)
                {
                    _decodeQueue.RemoveAt(idx);
                    _decodeQueue.Insert(0, item);
                }

                if (!_signalOutstanding)
                {
                    _signalOutstanding = true;
                    _queueSignal.Release(); // make sure the worker wakes for the re-ordered queue
                }
            }
        }

        /// <summary>Terminal state: no longer in any queue; further requests are no-ops (or retries when failed).</summary>
        private void FinishTile(TileTask tile, string cacheKey, bool failed)
        {
            lock (_tileLock)
            {
                tile.GateWaiter = null;
                tile.Extracting = false;
                tile.Decoding = false;
                tile.DecodeWork = null;
                if (failed)
                {
                    // Extraction/decode failed — drop the state so a later request can retry.
                    _tiles.Remove(cacheKey);
                }
                else
                {
                    tile.Done = true;
                }
            }
        }

        /// <summary>Number of work items waiting for decode (diagnostics; also used by tests).</summary>
        internal int QueueDepth
        {
            get { lock (_queueLock) { return _decodeQueue.Count; } }
        }

        // ── Live pipeline status (which file is being read right now) ──
        private int _extractingNow; // archives currently being extracted (slow stage)
        private int _decodedTotal;  // thumbnails decoded so far this session
        private string _activeSource = string.Empty;
        private int _workerDecoding; // 1 while the decode worker is inside one item's decode

        /// <summary>
        /// Test seam: true while the single decode worker is inside one work item's decode
        /// (between the pop and the callback), so tests can pin the schedule on the exact
        /// state "the worker is decoding source X right now" instead of sleeping.
        /// </summary>
        internal bool IsWorkerDecoding => Volatile.Read(ref _workerDecoding) == 1;

        /// <summary>Test seam: the source key the worker is currently decoding ("" when idle).</summary>
        internal string ActiveSource => _activeSource;
        private DateTime _lastStatusUtc = DateTime.MinValue;

        /// <summary>
        /// Live pipeline status sink. Every view model that displays pipeline status
        /// (the Comic Search and Search Image tabs share this provider) subscribes a
        /// handler that marshals to the UI thread. The payload is <see cref="StatusText"/>
        /// (or null when the pipeline is idle and the status line should hide).
        /// </summary>
        public event Action<string?>? StatusChanged;

        /// <summary>
        /// Human-readable pipeline snapshot for the status line: which file is being read right
        /// now, how many extractions run in parallel, decode queue depth and the loaded total.
        /// Empty when nothing is in flight (the status line hides itself).
        /// </summary>
        public string StatusText
        {
            get
            {
                var extracting = Math.Max(0, Volatile.Read(ref _extractingNow));
                if (_activeSource.Length == 0 && QueueDepth == 0 && extracting == 0)
                    return string.Empty;

                var text = "Thumbnails:";
                if (_activeSource.Length > 0)
                    text += $" reading {_activeSource}";
                if (extracting > 1)
                    text += $" (+{extracting - 1} more)";
                if (QueueDepth > 0)
                    text += $" — queue {QueueDepth}";
                if (_decodedTotal > 0)
                    text += $" — loaded {_decodedTotal}";
                return text;
            }
        }

        /// <summary>Fires <see cref="StatusChanged"/> (250 ms throttle for plain calls, <c>force</c> bypasses it).</summary>
        private void ReportStatus(bool force = false)
        {
            var now = DateTime.UtcNow;
            if (!force && (now - _lastStatusUtc) < TimeSpan.FromMilliseconds(250))
                return;
            _lastStatusUtc = now;
            StatusChanged?.Invoke(StatusText.Length > 0 ? StatusText : null);
        }

        /// <summary>"Name.cbz (page 5)" display form of a "{path}|page=N" source key.</summary>
        private static string DisplaySource(string sourceKey)
        {
            var sep = sourceKey.IndexOf("|page=", StringComparison.Ordinal);
            if (sep < 0) return Path.GetFileName(sourceKey);
            var page = int.TryParse(sourceKey[(sep + 6)..], out var p) ? p + 1 : 0;
            var name = Path.GetFileName(sourceKey[..sep]);
            return page > 1 ? $"{name} (page {page})" : name;
        }

        public void Enqueue(string path, int width, Action<ThumbnailResult?>? callback = null)
        {
            if (string.IsNullOrEmpty(path)) return;
            var targetWidth = width > 0 ? width : _settings.ThumbnailWidth;
            if (CacheTryGet(KeyWithWidth(path, targetWidth), out var cached))
            {
                // Already loaded — deliver the cached result (bitmap AND original size).
                // A silent return here used to leave brand-new items on the placeholder.
                Deliver(cached.Value, callback);
                return;
            }
            DecodeQueuePush(new WorkItem
            {
                CacheKey = KeyWithWidth(path, targetWidth),
                SourceKey = path,
                Width = targetWidth,
                Callback = callback,
                Generation = Volatile.Read(ref _generation)
            });
        }

        /// <summary>Marshals a cache-hit delivery to the UI thread (callbacks touch bound items).</summary>
        private static void Deliver(ThumbnailResult result, Action<ThumbnailResult?>? callback)
        {
            if (callback is null) return;
            PostToUi(() => callback(result));
        }

        public ThumbnailResult? GetBitmap(string path, int width, Action<ThumbnailResult?>? onLoaded = null)
        {
            var targetWidth = width > 0 ? width : _settings.ThumbnailWidth;

            if (string.IsNullOrEmpty(path))
            {
                var placeholder = CreatePlaceholder(targetWidth, _settings.ThumbnailHeight);
                var result = new ThumbnailResult(placeholder, 0, 0, 0);
                onLoaded?.Invoke(result);
                return result;
            }

            if (CacheTryGet(KeyWithWidth(path, targetWidth), out var cached))
            {
                return cached;
            }

            // queue background load and return placeholder
            DecodeQueuePush(new WorkItem
            {
                CacheKey = KeyWithWidth(path, targetWidth),
                SourceKey = path,
                Width = targetWidth,
                Callback = onLoaded,
                Generation = Volatile.Read(ref _generation)
            });
            var ph = CreatePlaceholder(targetWidth, _settings.ThumbnailHeight);
            var phResult = new ThumbnailResult(ph, 0, 0, 0);
            onLoaded?.Invoke(phResult);
            return phResult;
        }

        /// <summary>
        /// A frozen placeholder bitmap (configured thumbnail size, checkerboard) for showing in
        /// the image cell until the real thumbnail is decoded. Displaying a same-size
        /// placeholder keeps the cell size stable, so the later real-thumbnail swap re-renders
        /// the cell without triggering a results-panel re-layout.
        ///
        /// One shared instance is returned to every item: a frozen BitmapSource is thread-safe
        /// and shareable, so the 180x240 per-pixel fill runs exactly once. Generating one per
        /// item used to run that fill on the UI thread per result — for 7000 results that is
        /// tens of seconds of solid UI freeze.
        /// </summary>
        public BitmapSource GetPlaceholder()
        {
            lock (_placeholderLock)
            {
                var w = _settings.ThumbnailWidth;
                var h = _settings.ThumbnailHeight;
                if (_sharedPlaceholder is null
                    || _sharedPlaceholder.PixelWidth != w
                    || _sharedPlaceholder.PixelHeight != h)
                {
                    _sharedPlaceholder = CreatePlaceholder(w, h);
                }
                return _sharedPlaceholder;
            }
        }

        private BitmapSource? _sharedPlaceholder;
        private readonly object _placeholderLock = new();

        /// <summary>
        /// Extracts the page image (page index 0 = cover) of <paramref name="archivePath"/>
        /// into memory and enqueues its decode. The thumbnail cache key is page-specific —
        /// "{path}" for the cover, "{path}|page=N" otherwise (same convention as the
        /// embedding cache keys) — so pages of one archive never alias each other.
        ///
        /// Calling this again for a tile that is already in the pipeline (waiting for a slot,
        /// extracting, in the decode queue, or decoded) is a no-op or a PRIORITY BUMP to the
        /// front of the respective queue — this is how the UI signals "this tile is on
        /// screen right now" without triggering duplicate archive IO.
        /// </summary>
        public void EnsurePageAndEnqueue(string archivePath, int pageIndex, Action<ThumbnailResult?>? onLoaded = null)
        {
            if (string.IsNullOrEmpty(archivePath)) return;
            if (pageIndex < 0) pageIndex = 0;

            string sourceKey = pageIndex == 0 ? archivePath : $"{archivePath}|page={pageIndex}";
            int width = _settings.ThumbnailWidth;
            string cacheKey = KeyWithWidth(sourceKey, width);

            if (CacheTryGet(cacheKey, out var cached))
            {
                // Already loaded — deliver the cached result (bitmap AND original size) instead
                // of silently returning (a silent return left brand-new items on the placeholder).
                Deliver(cached.Value, onLoaded);
                return;
            }

            TileTask tile;
            bool startNew;
            lock (_tileLock)
            {
                if (!_tiles.TryGetValue(cacheKey, out tile))
                {
                    tile = new TileTask();
                    _tiles[cacheKey] = tile;
                }

                if (tile.Done || tile.Extracting || tile.Decoding)
                {
                    startNew = false; // in flight or finished — nothing to do (Decoding: it is in the worker's current batch)
                }
                else if (tile.GateWaiter is not null)
                {
                    BumpGateWaiter(tile);
                    startNew = false;
                }
                else if (tile.DecodeWork is not null)
                {
                    BumpDecodeQueue(tile);
                    startNew = false;
                }
                else
                {
                    // Slot taken (or parked on the LIFO gate) AT REQUEST TIME — the push
                    // order decides who extracts first.
                    TakeSlotOrPark(tile);
                    startNew = true;
                }
            }

            if (startNew)
            {
                _ = Task.Run(() => RunExtractionAsync(tile, archivePath, pageIndex, sourceKey, cacheKey, width, onLoaded));
            }
        }

        /// <summary>One extraction task: wait for the gate grant (if parked), extract the page, hand the bytes to the decode queue. Work finishing after the search generation changed (Clear) discards its result.</summary>
        private async Task RunExtractionAsync(TileTask tile, string archivePath, int pageIndex, string sourceKey, string cacheKey, int width, Action<ThumbnailResult?>? onLoaded)
        {
            int gen = Volatile.Read(ref _generation);
            try
            {
                if (CacheTryGet(cacheKey, out var cached))
                {
                    // Loaded meanwhile. Release the slot only if we actually held one
                    // (a parked, ungranted waiter must NOT hand a slot to someone else).
                    var held = false;
                    lock (_tileLock)
                    {
                        held = tile.Extracting;
                        if (tile.GateWaiter is { } w) w.TrySetCanceled(); // let LifoRelease skip it (no slot leak)
                        tile.GateWaiter = null;
                        tile.Extracting = false;
                        tile.Done = true;
                    }
                    if (held)
                    {
                        LifoRelease();
                    }
                    Deliver(cached.Value, onLoaded); // this request's item still needs the bitmap
                    return;
                }

                if (tile.GateWaiter is { } parked)
                {
                    // Parked on the LIFO gate: wait until the newest-first grant arrives.
                    using (_cts.Token.Register(() => parked.TrySetCanceled()));
                    try
                    {
                        await parked.Task.ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_tileLock)
                        {
                            if (ReferenceEquals(tile.GateWaiter, parked)) tile.GateWaiter = null;
                            tile.Extracting = true; // the slot was transferred to us by the grant
                        }
                    }
                }

                bool started = false;
                try
                {
                    // The search moved on (Clear) before we started — discard: no status,
                    // no decode; the slot is still released in the outer finally.
                    if (gen != Volatile.Read(ref _generation)) return;

                    Interlocked.Increment(ref _extractingNow);
                    started = true;
                    _activeSource = DisplaySource(sourceKey);
                    ReportStatus(true); // a new file is being read — show it immediately

                    var bytes = await _coverService.ExtractPageAsync(archivePath, pageIndex).ConfigureAwait(false);

                    // Finished after the search moved on — discard (the new search owns the
                    // status line and the queues now).
                    if (gen != Volatile.Read(ref _generation)) return;

                    if (bytes is null || bytes.Length == 0)
                    {
                        // Nothing to decode — terminal for this tile (no retry, like a missing file).
                        FinishTile(tile, cacheKey, failed: false);
                        if (onLoaded is not null)
                        {
                            PostToUi(() => onLoaded(null));
                        }
                        return;
                    }

                    // Enqueue decoding of the in-memory page bytes
                    var work = new WorkItem
                    {
                        CacheKey = cacheKey,
                        SourceKey = sourceKey,
                        Data = bytes,
                        Width = width,
                        Callback = onLoaded,
                        Tile = tile,
                        Generation = gen
                    };
                    lock (_tileLock)
                    {
                        tile.DecodeWork = work;
                    }
                    DecodeQueuePush(work);
                }
                finally
                {
                    LifoRelease();
                    if (started)
                    {
                        Interlocked.Decrement(ref _extractingNow);
                        if (gen == Volatile.Read(ref _generation))
                        {
                            ReportStatus();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                lock (_tileLock)
                {
                    tile.Extracting = false;
                    tile.GateWaiter = null;
                }
            }
            catch (Exception ex)
            {
                FinishTile(tile, cacheKey, failed: true); // drop the state so a later request can retry
                _logger.LogDebug(ex, "EnsurePageAndEnqueue failed for {ArchivePath} page {PageIndex}", archivePath, pageIndex);
            }
        }

        /// <summary>
        /// Clears the LRU cache, the pending decode queue, the tile state and the extraction
        /// gate (all, like the proven AntiDupl provider), and — via the generation bump —
        /// makes in-flight work of the previous search discard its result: it neither re-queues
        /// decodes nor touches the status line, so a new search starts from a clean status.
        /// Parked gate waiters are cancelled immediately; extractions already in flight finish
        /// their (slow) archive read and then exit quietly.
        /// </summary>
        public void Clear()
        {
            Interlocked.Increment(ref _generation); // stale in-flight work discards its results
            lock (_cacheLock)
            {
                _cache.Clear();
                _cacheOrder.Clear();
            }
            lock (_queueLock)
            {
                _decodeQueue.Clear();
            }
            lock (_tileLock)
            {
                _tiles.Clear();
            }
            lock (_extractGateLock)
            {
                // Wake the old search's parked waiters so their tasks exit.
                while (_extractWaiters.Count > 0)
                {
                    _extractWaiters.Pop().TrySetCanceled();
                }
                _extractFreeSlots = _maxExtractions; // a fresh gate for the new search
            }
            Interlocked.Exchange(ref _decodedTotal, 0); // new search = fresh "loaded" counter
            _activeSource = string.Empty;
            ReportStatus(true);
        }

        /// <summary>
        /// Marshals <paramref name="action"/> to the WPF UI thread. In a non-WPF host
        /// (unit tests: no Application instance) it runs inline on the calling thread so
        /// callbacks stay observable and the worker never dies on a null dispatcher.
        /// </summary>
        private static void PostToUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                action();
                return;
            }

            dispatcher.BeginInvoke(action);
        }

        private static string KeyWithWidth(string key, int width) => $"{key}#{width}";

        private void DecodeQueuePush(WorkItem work)
        {
            lock (_queueLock)
            {
                _decodeQueue.Insert(0, work); // index 0 = newest; the worker drains the list front-to-back (LIFO)
                if (!_signalOutstanding)
                {
                    _signalOutstanding = true;
                    _queueSignal.Release(); // single outstanding token keeps the semaphore bounded
                }
            }
        }

        /// <summary>LRU get: a hit moves the key to the most-recently-used end.</summary>
        private bool CacheTryGet(string key, out ThumbnailResult? entry)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var hit))
                {
                    _cacheOrder.Remove(key);
                    _cacheOrder.Add(key);
                    entry = hit;
                    return true;
                }
                entry = null;
                return false;
            }
        }

        /// <summary>Adds an entry and evicts the least recently used entries beyond the limit.</summary>
        private void CacheAdd(string key, ThumbnailResult entry, int maxCached)
        {
            lock (_cacheLock)
            {
                if (_cache.ContainsKey(key))
                {
                    _cache[key] = entry;
                    _cacheOrder.Remove(key);
                    _cacheOrder.Add(key);
                    return;
                }

                _cache[key] = entry;
                _cacheOrder.Add(key);

                while (maxCached > 0 && _cache.Count > maxCached)
                {
                    string oldest = _cacheOrder[0];
                    _cacheOrder.RemoveAt(0);
                    _cache.Remove(oldest);
                }
            }
        }

        /// <summary>Original pixel size from the raw page bytes (header-only parse, no pixel decode); (0, 0) when the format is unknown.</summary>
        private static (int Width, int Height) TryIdentifySize(byte[] data)
        {
            try
            {
                using var ms = new MemoryStream(data);
                var info = Image.Identify(ms);
                return (info.Width, info.Height);
            }
            catch
            {
                return (0, 0);
            }
        }

        /// <summary>Original pixel size from the file (header-only parse, no pixel decode); (0, 0) when the format is unknown.</summary>
        private static (int Width, int Height) TryIdentifySize(string path)
        {
            try
            {
                var info = Image.Identify(path);
                return (info.Width, info.Height);
            }
            catch
            {
                return (0, 0);
            }
        }

        private BitmapSource CreatePlaceholder(int width, int height)
        {
            int finalW = Math.Max(8, width);
            int finalH = Math.Max(8, height > 0 ? height : (int)(finalW * 1.333));

            try
            {
                var dpi = 96;
                var pixelFormat = PixelFormats.Pbgra32;
                var stride = (finalW * pixelFormat.BitsPerPixel + 7) / 8;
                var pixels = new byte[stride * finalH];

                // Fill background with checkerboard-like pattern
                for (int y = 0; y < finalH; y++)
                {
                    for (int x = 0; x < finalW; x++)
                    {
                        bool dark = ((x / 8) + (y / 8)) % 2 == 0;
                        var offset = y * stride + x * 4;
                        if (dark)
                        {
                            pixels[offset + 0] = 0xE6; // B
                            pixels[offset + 1] = 0xE6; // G
                            pixels[offset + 2] = 0xE6; // R
                            pixels[offset + 3] = 0xFF; // A
                        }
                        else
                        {
                            pixels[offset + 0] = 0xFF;
                            pixels[offset + 1] = 0xFF;
                            pixels[offset + 2] = 0xFF;
                            pixels[offset + 3] = 0xFF;
                        }
                    }
                }

                var bmp = BitmapSource.Create(finalW, finalH, dpi, dpi, pixelFormat, null, pixels, stride);
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[4], 4);
            }
        }

        private async Task WorkerAsync()
        {
            try
            {
                // Perf diagnostics: how fast the single decode worker digests the queue after
                // a large search (queue depth = extraction outrunning decode). The counter
                // doubles as the "loaded" figure of the public <see cref="StatusText"/>.
                var lastLogUtc = DateTime.MinValue;
                while (!_cts.Token.IsCancellationRequested)
                {
                    // Signal-driven wake-up (no polling). After the wake the whole pending
                    // queue is drained at once, newest first (LIFO).
                    await _queueSignal.WaitAsync(_cts.Token);

                    List<WorkItem> batch;
                    lock (_queueLock)
                    {
                        _signalOutstanding = false;
                        batch = new List<WorkItem>(_decodeQueue);
                        _decodeQueue.Clear();
                    }

                    // Mark the batch in-flight so concurrent visibility bumps cannot
                    // mistake these tiles for "not in the pipeline" and re-extract them.
                    lock (_tileLock)
                    {
                        foreach (var work in batch)
                        {
                            if (work.Tile is not null)
                            {
                                work.Tile.Decoding = true;
                            }
                        }
                    }

                    foreach (var work in batch)
                    {
                        if (_cts.Token.IsCancellationRequested) break;

                        // The search moved on while this was queued — drop it silently
                        // (no callback, no cache, no status/counter updates).
                        if (work.Generation != Volatile.Read(ref _generation)) continue;

                        try
                        {
                            Volatile.Write(ref _workerDecoding, 1);
                            _activeSource = DisplaySource(work.SourceKey); // this file is being decoded now
                            ReportStatus();
                            BitmapSource? image;
                            if (work.Data is not null)
                            {
                                // Page extracted in memory — decode from bytes (OnLoad fully decodes, stream can close)
                                using var stream = new MemoryStream(work.Data);
                                var bmp = new BitmapImage();
                                bmp.BeginInit();
                                bmp.CacheOption = BitmapCacheOption.OnLoad;
                                bmp.StreamSource = stream;
                                bmp.DecodePixelWidth = work.Width;
                                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                                bmp.EndInit();
                                bmp.Freeze();
                                image = bmp;
                            }
                            else
                            {
                                if (!File.Exists(work.SourceKey))
                                {
                                    // notify null
                                    if (work.Callback is not null)
                                    {
                                        PostToUi(() => work.Callback(null));
                                    }
                                    continue;
                                }

                                var bmp = new BitmapImage();
                                bmp.BeginInit();
                                bmp.CacheOption = BitmapCacheOption.OnLoad;
                                bmp.UriSource = new Uri(work.SourceKey);
                                bmp.DecodePixelWidth = work.Width;
                                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                                bmp.EndInit();
                                bmp.Freeze();
                                image = bmp;
                            }

                            // Original size of the displayed image: the scaled bitmap above no
                            // longer carries it, so read it from the raw source — the extracted
                            // page bytes for archive results, the file itself otherwise. Header-only
                            // parse (no pixel decode); ByteSize = page bytes / file size on disk.
                            int srcWidth = 0, srcHeight = 0;
                            long srcBytes = 0;
                            if (work.Data is not null)
                            {
                                srcBytes = work.Data.Length;
                                (srcWidth, srcHeight) = TryIdentifySize(work.Data);
                            }
                            else
                            {
                                try { srcBytes = new FileInfo(work.SourceKey).Length; }
                                catch { /* best effort — the size line simply falls back */ }
                                (srcWidth, srcHeight) = TryIdentifySize(work.SourceKey);
                            }
                            var result = new ThumbnailResult(image, srcWidth, srcHeight, srcBytes);

                            CacheAdd(work.CacheKey, result, _settings.MaxCachedThumbnails);

                            var decodedTotal = Interlocked.Increment(ref _decodedTotal);
                            var nowUtc = DateTime.UtcNow;
                            if (decodedTotal % 256 == 0 || nowUtc - lastLogUtc > TimeSpan.FromSeconds(5))
                            {
                                _logger.LogInformation(
                                    "[Perf] Thumbnails: {Decoded} decoded so far, decode queue depth {Depth}",
                                    decodedTotal, QueueDepth);
                                lastLogUtc = nowUtc;
                            }

                            if (work.Callback is not null)
                            {
                                PostToUi(() => work.Callback(result));
                            }

                            if (work.Tile is not null)
                            {
                                FinishTile(work.Tile, work.CacheKey, failed: false);
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Thumbnail load failed");
                            if (work.Tile is not null)
                            {
                                FinishTile(work.Tile, work.CacheKey, failed: true); // allow a later retry
                            }
                        }
                        finally
                        {
                            Volatile.Write(ref _workerDecoding, 0);
                        }
                    }

                    // Backlog drained: let the status line settle (or hide when nothing is left).
                    if (QueueDepth == 0 && Math.Max(0, Volatile.Read(ref _extractingNow)) == 0)
                    {
                        ReportStatus(true);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _worker.Wait(2000); } catch { }
            _queueSignal.Dispose();
        }

        /// <summary>One decode unit; index 0 of the queue list is the newest (decoded first).</summary>
        private sealed class WorkItem
        {
            public string CacheKey = string.Empty;
            public string SourceKey = string.Empty;
            public byte[]? Data; // null = decode straight from the SourceKey file
            public int Width;
            public Action<ThumbnailResult?>? Callback;
            public TileTask? Tile; // null for file-only Enqueue/GetBitmap requests
            public int Generation; // search generation the item was queued for (Clear() bumps it)
        }
        /// <summary>
        /// Pipeline position of one cacheKey: gate waiter (waiting for an extraction slot),
        /// extracting (holds a slot), in the decode queue (DecodeWork), in the worker's
        /// batch (Decoding) or done. Lets repeat requests re-order instead of duplicating.
        /// All members guarded by _tileLock.
        /// </summary>
        private sealed class TileTask
        {
            public TaskCompletionSource<bool>? GateWaiter;
            public bool Extracting;
            public bool Decoding;
            public bool Done;
            public WorkItem? DecodeWork;
        }
    }
}