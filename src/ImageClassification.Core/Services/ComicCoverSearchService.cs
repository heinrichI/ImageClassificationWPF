using System.Collections.Concurrent;
using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using Microsoft.Extensions.Logging;

namespace ImageClassification.Core.Services;

/// <summary>
/// Scans CBZ/CBR archives and ranks their covers (or all pages) by CLIP
/// cosine similarity against a user text query.
/// Uses BatchImageEncoder for batched GPU inference and IVectorStore for caching.
///
/// Pipeline: each archive is opened exactly once via IArchiveReader.OpenSessionAsync;
/// image entries are enumerated once from the open handle, cache hits are taken
/// directly from the vector store, and only misses are extracted (from the same
/// open handle) and fed to the GPU batch encoder. There is no separate page-count
/// phase — the progress bar counts processed archives.
/// </summary>
internal sealed class ComicCoverSearchService : IComicCoverSearchService
{
    private const string ModelName = "ClipViTB32";
    private const int ArchiveParallelism = 4; // archives opened/processed concurrently
    private static readonly string[] ArchivePatterns = { "*.cbz", "*.cbr", "*.cb7", "*.cbt" };

    private readonly IArchiveReader _archiveReader;
    private readonly IClipTextEncoder _textEncoder;
    private readonly IVectorStore _store;
    private readonly BatchImageEncoder _batchEncoder;
    private readonly ILogger<ComicCoverSearchService> _logger;

    /// <summary>
    /// In-memory page counts learned from archives opened during this app session.
    /// Lets re-runs of an all-pages search probe the cache first and skip the
    /// archive open entirely when every page is already cached.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _seenPageCounts = new();

    public ComicCoverSearchService(
        IArchiveReader archiveReader,
        IClipTextEncoder textEncoder,
        IVectorStore store,
        BatchImageEncoder batchEncoder,
        ILogger<ComicCoverSearchService> logger)
    {
        _archiveReader = archiveReader ?? throw new ArgumentNullException(nameof(archiveReader));
        _textEncoder = textEncoder ?? throw new ArgumentNullException(nameof(textEncoder));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _batchEncoder = batchEncoder ?? throw new ArgumentNullException(nameof(batchEncoder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── Cancellation-safe reporting helpers (В2) ────────────────────────

    /// <summary>
    /// Reports a status phase unless the operation has been cancelled,
    /// so late reports can't overwrite the "Search cancelled." UI state.
    /// </summary>
    private static void ReportPhase(IProgress<ComicSearchStatus>? status, CancellationToken ct, string phase)
    {
        if (status is not null && !ct.IsCancellationRequested)
            status.Report(new ComicSearchStatus { Phase = phase });
    }

    /// <summary>
    /// Reports a progress-bar tick (processed / found archives) unless the operation
    /// has been cancelled.
    /// </summary>
    private static void ReportProgressTick(IProgress<ComicSearchStatus>? status, CancellationToken ct,
        int current, int total)
    {
        if (status is not null && !ct.IsCancellationRequested)
            status.Report(new ComicSearchStatus { Current = current, Total = total });
    }

    /// <inheritdoc />
    public Task LoadModelAsync(string clipOnnxPath)
    {
        // No-op — model loading is delegated to BatchImageEncoder (lazy)
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<List<ComicCoverResult>> SearchAsync(
        string directoryPath,
        string query,
        IProgress<ComicSearchStatus>? status = null,
        CancellationToken ct = default)
    {
        return SearchCoreAsync(directoryPath, query, allPages: false, status, ct);
    }

    /// <inheritdoc />
    public Task<List<ComicCoverResult>> SearchAllPagesAsync(
        string directoryPath,
        string query,
        IProgress<ComicSearchStatus>? status = null,
        CancellationToken ct = default)
    {
        return SearchCoreAsync(directoryPath, query, allPages: true, status, ct);
    }

    /// <summary>
    /// Shared pipeline for both modes. The only difference: covers mode processes
    /// page 0 of each archive, all-pages mode processes every enumerated page.
    /// </summary>
    private async Task<List<ComicCoverResult>> SearchCoreAsync(
        string directoryPath,
        string query,
        bool allPages,
        IProgress<ComicSearchStatus>? status,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var label = allPages ? "SearchAllPages" : "Search";
        _logger.LogInformation(
            "[{Label}] Started — query='{Query}' dir='{Dir}' vectorCache='{CachePath}'",
            label, query, directoryPath, _store.DatabasePath ?? "<in-memory>");

        if (!Directory.Exists(directoryPath))
        {
            _logger.LogWarning("[{Label}] Directory not found: {Dir}", label, directoryPath);
            return new List<ComicCoverResult>();
        }

        // 1. Scan archives
        ReportPhase(status, ct, "Scanning archives...");
        var t0 = sw.Elapsed;
        var archiveFiles = ScanArchivesRecursive(directoryPath);
        _logger.LogInformation("[{Label}] Scan: {Count} archives found in {Ms}ms",
            label, archiveFiles.Count, (sw.Elapsed - t0).TotalMilliseconds);

        if (archiveFiles.Count == 0)
        {
            ReportPhase(status, ct, "No comic archives found in the selected directory");
            return new List<ComicCoverResult>();
        }

        ReportPhase(status, ct, archiveFiles.Count == 1
            ? "Found 1 archive"
            : $"Found {archiveFiles.Count} archives");

        // 2. Encode the text query once
        ReportPhase(status, ct, "Encoding query text...");
        t0 = sw.Elapsed;
        var queryEmbedding = await _textEncoder
            .EncodeTextAsync($"a photo of {query}", ct).ConfigureAwait(false);
        _logger.LogInformation("[{Label}] Text encoding: {Ms}ms", label, (sw.Elapsed - t0).TotalMilliseconds);

        // 3. Process archives — one open handle per archive, progress = archive count
        int total = archiveFiles.Count;
        ReportPhase(status, ct, "Processing archives...");
        ReportProgressTick(status, ct, 0, total);
        t0 = sw.Elapsed;

        var results = new List<ComicCoverResult>();
        var resultsLock = new object();

        await RunArchivePipelineAsync(
                archiveFiles,
                allPages,
                queryEmbedding,
                r =>
                {
                    lock (resultsLock) results.Add(r);
                },
                (done, _) => ReportProgressTick(status, ct, done, total),
                status,
                ct)
            .ConfigureAwait(false);

        _logger.LogInformation("[{Label}] Pipeline processed {Count} archive(s) in {Ms}ms",
            label, total, (sw.Elapsed - t0).TotalMilliseconds);

        // 4. Rank
        ReportPhase(status, ct, "Scoring and ranking results...");
        var output = results.OrderByDescending(x => x.SimilarityScore).ToList();

        ReportProgressTick(status, ct, total, total);
        _logger.LogInformation("[{Label}] Done: {Results} results, top score={TopScore:F4}, total={TotalMs}ms",
            label, output.Count,
            output.Count > 0 ? output.Max(x => x.SimilarityScore) : 0f,
            sw.ElapsedMilliseconds);

        return output;
    }

    /// <summary>
    /// Producer/consumer pipeline over archives.
    /// Producer (parallel, up to <see cref="ArchiveParallelism"/> archives at a time):
    /// probes the embedding cache FIRST and opens the archive only when the cache
    /// cannot answer the request — covers mode skips the open when the cover is
    /// cached; all-pages mode skips it when every page of a previously-seen archive
    /// is cached (see <see cref="_seenPageCounts"). On a cache miss the archive is
    /// opened exactly once via <c>OpenSessionAsync</c>, misses are extracted from the
    /// same handle and enqueued.
    /// Consumer: drains the bounded queue in GPU batches, saves embeddings and
    /// reports results as soon as each batch is scored.
    /// </summary>
    private async Task RunArchivePipelineAsync(
        List<string> archiveFiles,
        bool allPages,
        float[] queryEmbedding,
        Action<ComicCoverResult> onResult,
        Action<int, int> onArchiveProcessed,
        IProgress<ComicSearchStatus>? status,
        CancellationToken ct)
    {
        int queueCapacity = Math.Max(1, _batchEncoder.BatchSize * 2);
        using var queue = new BlockingCollection<ImageWorkItem>(queueCapacity);

        int processedArchives = 0;
        int enqueued = 0;
        int cacheHits = 0;
        int cacheMisses = 0;
        int failedExtractions = 0;
        int flushedBatches = 0;

        // Throttled (250 ms) reporter for high-frequency detail lines:
        // per-archive completion and GPU-queue snapshots shown verbatim in the UI.
        var detailStatus = new ThrottledStatusReporter(status, ct);

        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                        archiveFiles,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = Math.Max(1, Math.Min(ArchiveParallelism, Environment.ProcessorCount)),
                            CancellationToken = ct
                        },
                        async (path, token) =>
                        {
                            token.ThrowIfCancellationRequested();

                            DateTime lastModified;
                            long fileSize;
                            try
                            {
                                var fi = new FileInfo(path);
                                lastModified = fi.LastWriteTimeUtc;
                                fileSize = fi.Length;
                            }
                            catch
                            {
                                lastModified = DateTime.MinValue;
                                fileSize = 0;
                            }

                            // ── Cache-first: answer from the vector store without touching the archive ──

                            if (allPages)
                            {
                                // Previously-seen archive: probe every learned page; if all hit,
                                // no archive open is needed at all.
                                if (_seenPageCounts.TryGetValue(path, out int seen) && seen > 0)
                                {
                                    var probe = await BatchGetCacheAsync(
                                        BuildCacheEntries(path, seen, true, lastModified, fileSize)).ConfigureAwait(false);

                                    if (IsFullyCached(path, seen, true, probe))
                                    {
                                        for (int p = 0; p < seen; p++)
                                        {
                                            token.ThrowIfCancellationRequested();
                                            Interlocked.Increment(ref cacheHits);
                                            onResult(ToResult(path, p, seen, probe[CacheKeyFor(path, p, true)]!, queryEmbedding));
                                        }

                                        _logger.LogInformation(
                                            "[Pipeline] {Name}: all {Pages} page(s) cached — archive not opened",
                                            Path.GetFileName(path), seen);

                                        ReportArchiveDone();
                                        return;
                                    }
                                }
                            }
                            else
                            {
                                // Covers mode: a single cache probe for page 0.
                                string coverKey = CacheKeyFor(path, 0, false);
                                var probe = await BatchGetCacheAsync(
                                    BuildCacheEntries(path, 1, false, lastModified, fileSize)).ConfigureAwait(false);

                                if (probe.TryGetValue(coverKey, out var coverHit) && coverHit is not null)
                                {
                                    Interlocked.Increment(ref cacheHits);
                                    // Page count is unknown (archive not opened) — 0 makes the
                                    // UI page label render empty, which is correct for covers.
                                    onResult(ToResult(path, 0, 0, coverHit, queryEmbedding));

                                    _logger.LogDebug(
                                        "[Pipeline] {Name}: cover cached — archive not opened", Path.GetFileName(path));

                                    ReportArchiveDone();
                                    return;
                                }

                                Interlocked.Increment(ref cacheMisses);
                            }

                            // ── Slow path: the cache cannot answer — open the archive (once) ──

                            using var session = await _archiveReader
                                .OpenSessionAsync(path).ConfigureAwait(false);

                            // Open-stage diagnostics (open failures, extension/container
                            // mismatches) — straight to the UI notes list and the log.
                            // Bypasses the throttled reporter on purpose: notes are rare
                            // per-search events and must never be dropped. Remember the
                            // value so the post-bulk check below cannot re-report it.
                            string? noteAfterOpen = session.LastNote;
                            if (noteAfterOpen is not null)
                            {
                                _logger.LogInformation("[Pipeline] {Note}", noteAfterOpen);
                                status?.Report(new ComicSearchStatus
                                {
                                    Note = noteAfterOpen
                                });
                            }

                            int pageCount = session.ImageEntries.Count;

                            if (pageCount == 0)
                            {
                                _logger.LogWarning(
                                    "[Pipeline] No image entries — archive skipped: {Path}", path);
                            }
                            else
                            {
                                int pageEnd = allPages ? pageCount : 1; // covers mode: page 0 only
                                if (allPages)
                                    _seenPageCounts[path] = pageCount;

                                // Batch-check the embedding cache for the pages to process
                                var cached = await BatchGetCacheAsync(
                                    BuildCacheEntries(path, pageEnd, allPages, lastModified, fileSize)).ConfigureAwait(false);

                                // Variant B: if more than half of the pages are cache misses,
                                // extract the archive in ONE bulk pass — for solid 7z archives
                                // per-entry extraction re-decodes the whole solid prefix for
                                // every entry (O(N²)), while a single full pass is O(N).
                                // Covers mode (pageEnd == 1) always extracts per page.
                                int missCount = 0;
                                for (int p = 0; p < pageEnd; p++)
                                {
                                    string probeKey = CacheKeyFor(path, p, allPages);
                                    if (!cached.TryGetValue(probeKey, out var probe) || probe is null)
                                        missCount++;
                                }

                                byte[]?[]? bulk = pageEnd > 1 && missCount * 2 > pageEnd
                                    ? await session.ExtractAllToMemoryAsync().ConfigureAwait(false)
                                    : null;

                                // Bulk-stage diagnostics (retry mismatch, per-entry fallback)
                                // — only reported when the note changed since open time,
                                // so an open-stage mismatch note is never double-reported.
                                if (bulk is not null && session.LastNote is not null
                                    && session.LastNote != noteAfterOpen)
                                {
                                    _logger.LogInformation("[Pipeline] {Note}", session.LastNote);
                                    status?.Report(new ComicSearchStatus
                                    {
                                        Note = session.LastNote
                                    });
                                }

                                int archiveHits = 0;
                                int archiveMisses = 0;

                                for (int p = 0; p < pageEnd; p++)
                                {
                                    token.ThrowIfCancellationRequested();
                                    string cacheKey = CacheKeyFor(path, p, allPages);

                                    if (cached.TryGetValue(cacheKey, out var hit) && hit is not null)
                                    {
                                        archiveHits++;
                                        Interlocked.Increment(ref cacheHits);
                                        onResult(ToResult(path, p, pageCount, hit, queryEmbedding));
                                        continue;
                                    }

                                    // Cache miss — take bytes from the bulk pass (when used)
                                    // or extract from the already-open session handle
                                    // (per-page misses are aggregated into the per-archive and
                                    // final "Cache summary" log lines — no per-page log flood)
                                    Interlocked.Increment(ref cacheMisses);
                                    archiveMisses++;
                                    byte[]? bytes = bulk is not null
                                        ? bulk[p]
                                        : await session.ExtractToMemoryAsync(p).ConfigureAwait(false);
                                    if (bytes is null || bytes.Length == 0)
                                    {
                                        Interlocked.Increment(ref failedExtractions);
                                        _logger.LogWarning(
                                            "[Pipeline] Empty image bytes extracted: {Path} page {PageIndex}", path, p);
                                        continue;
                                    }

                                    //_logger.LogWarning("Not found in cache {Path} {CacheKey}", path, cacheKey);
                                    var workItem = new ImageWorkItem(path, p, pageCount, cacheKey, lastModified, fileSize, bytes);
                                    // Blocking add: backpressure on the bounded queue, respects the token
                                    queue.Add(workItem, token);
                                    Interlocked.Increment(ref enqueued);
                                }

                                _logger.LogInformation(
                                    "[Pipeline] {Name}: {Hits}/{Total} page(s) from cache{Misses}",
                                    Path.GetFileName(path), archiveHits, pageEnd,
                                    archiveMisses > 0 ? $", {archiveMisses} cache miss" : string.Empty);
                            }

                            void ReportArchiveDone()
                            {
                                int done = Interlocked.Increment(ref processedArchives);
                                onArchiveProcessed(done, archiveFiles.Count);
                                int batches = Volatile.Read(ref flushedBatches);
                                detailStatus.Report(new ComicSearchStatus
                                {
                                    Detail = FormatGpuDetail(done, archiveFiles.Count, batches, queue.Count),
                                    Current = done,
                                    Total = archiveFiles.Count,
                                    GpuBatchesDone = batches,
                                    GpuQueueDepth = queue.Count
                                });
                                _logger.LogInformation(
                                    "[Pipeline] Archive {Done}/{Total} processed: {Name} ({PageCount} pages)",
                                    done, archiveFiles.Count, Path.GetFileName(path),
                                    allPages ? _seenPageCounts.GetValueOrDefault(path) : 0);
                            }

                            ReportArchiveDone();
                        }).ConfigureAwait(false);
            }
            finally
            {
                // Always complete the queue so the consumer can drain and finish,
                // even when the producer was cancelled or a body threw.
                queue.CompleteAdding();
            }
        }, ct);

        var batch = new List<ImageWorkItem>(_batchEncoder.BatchSize);

        async Task FlushBatchAsync()
        {
            if (batch.Count == 0)
                return;

            int flushNumber = Interlocked.Increment(ref flushedBatches);
            _logger.LogInformation("[Pipeline] Flushing batch #{BatchNumber} with {Count} images", flushNumber, batch.Count);

            var payload = new List<(int Index, byte[] Data)>(batch.Count);
            for (int i = 0; i < batch.Count; i++)
                payload.Add((i, batch[i].ImageData));

            var embeddings = await _batchEncoder
                .EncodeImagesFromMemoryBatchAsync(payload, progress: null, ct)
                .ConfigureAwait(false);

            for (int i = 0; i < batch.Count; i++)
            {
                var work = batch[i];
                var embedding = embeddings[i];
                if (embedding is null)
                    continue;

                onResult(ToResult(work.ArchivePath, work.PageIndex, work.PageCount, embedding, queryEmbedding));

                try
                {
                    await _store
                        .SaveAsync(ModelName, work.CacheKey, work.LastModified, work.FileSize, embedding)
                        .ConfigureAwait(false);
                    //_logger.LogInformation(
                        //"Saved to cache {CacheKey} {LastModified} {FileSize}",
                        //work.CacheKey, work.LastModified, work.FileSize);
                }
                catch
                {
                    // Best effort cache save
                }
            }

            batch.Clear();
        }

        foreach (var item in queue.GetConsumingEnumerable(ct))
        {
            batch.Add(item);
            if (batch.Count >= _batchEncoder.BatchSize)
            {
                await FlushBatchAsync().ConfigureAwait(false);
                if (queue.Count == 0)
                {
                    // GPU caught up — no backlog in front of it
                    int done = Volatile.Read(ref processedArchives);
                    int batches = Volatile.Read(ref flushedBatches);
                    detailStatus.Report(new ComicSearchStatus
                    {
                        Detail = FormatGpuDetail(done, archiveFiles.Count, batches, 0),
                        Current = done,
                        Total = archiveFiles.Count,
                        GpuBatchesDone = batches,
                        GpuQueueDepth = 0
                    });
                }
            }
        }

        await FlushBatchAsync().ConfigureAwait(false);
        await producer.ConfigureAwait(false);

        _logger.LogInformation(
            "[Pipeline] Completed: archives={Archives}, enqueued={Enqueued}, cacheHits={Hits}, failedExtractions={Failed}, flushedBatches={Batches}",
            archiveFiles.Count, enqueued, cacheHits, failedExtractions, flushedBatches);

        // One-line answer to "is the cache working?" — a re-search of the same
        // directory should show a hit rate near 100%.
        int hits = Volatile.Read(ref cacheHits);
        int misses = Volatile.Read(ref cacheMisses);
        int pages = hits + misses;
        _logger.LogInformation(
            "[Pipeline] Cache summary: {Hits} hit(s), {Misses} miss(es){Rate} across {Archives} archive(s)",
            hits, misses,
            pages > 0 ? $" ({100.0 * hits / pages:F1}% hit rate)" : string.Empty,
            archiveFiles.Count);
    }

    /// <inheritdoc />
    public async Task<byte[]?> ExtractCoverAsync(string archivePath)
    {
        using var session = await _archiveReader.OpenSessionAsync(archivePath).ConfigureAwait(false);
        return await session.ExtractToMemoryAsync(0).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ClearCacheAsync()
    {
        try
        {
            await _store.ClearAsync(ModelName).ConfigureAwait(false);
            _logger.LogInformation("Cleared vector cache for model {ModelName}", ModelName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear vector cache for model {ModelName}", ModelName);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Embedding cache key — kept compatible with keys written by previous versions:
    /// covers mode caches under the bare archive path, all-pages mode under "{path}|page={index}".
    /// </summary>
    private static string CacheKeyFor(string archivePath, int pageIndex, bool allPages)
        => allPages ? $"{archivePath}|page={pageIndex}" : archivePath;

    /// <summary>Builds the probe entries for pages [0, pageEnd) of an archive.</summary>
    private static List<(string FilePath, DateTime LastModified, long FileSize)> BuildCacheEntries(
        string archivePath, int pageEnd, bool allPages, DateTime lastModified, long fileSize)
    {
        var entries = new List<(string, DateTime, long)>(pageEnd);
        for (int p = 0; p < pageEnd; p++)
            entries.Add((CacheKeyFor(archivePath, p, allPages), lastModified, fileSize));
        return entries;
    }

    /// <summary>True when every page in [0, pageEnd) has a fresh, non-null cached vector.</summary>
    private static bool IsFullyCached(
        string archivePath, int pageEnd, bool allPages, Dictionary<string, float[]?> cached)
    {
        for (int p = 0; p < pageEnd; p++)
        {
            if (!cached.TryGetValue(CacheKeyFor(archivePath, p, allPages), out var vec) || vec is null)
                return false;
        }
        return true;
    }

    private static ComicCoverResult ToResult(
        string archivePath, int pageIndex, int pageCount, float[] embedding, float[] queryEmbedding)
    {
        return new ComicCoverResult
        {
            ArchivePath = archivePath,
            ArchiveFileName = Path.GetFileName(archivePath),
            SimilarityScore = CosineSimilarity(embedding, queryEmbedding),
            PageIndex = pageIndex,
            PageCount = pageCount
        };
    }

    private async Task<Dictionary<string, float[]?>> BatchGetCacheAsync(
        List<(string FilePath, DateTime LastModified, long FileSize)> entries)
    {
        try
        {
            return await _store.GetBatchAsync(ModelName, entries).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A throwing store (locked/corrupt DB) would silently turn every probe
            // into a miss — surface it so it cannot masquerade as "cache not working".
            _logger.LogWarning(ex,
                "[Pipeline] Vector store batch probe failed — {Count} page(s) treated as misses", entries.Count);
            return new Dictionary<string, float[]?>();
        }
    }

    /// <summary>
    /// Builds the full status line (shown verbatim by the UI — no trailing "...")
    /// with the archive counter and the GPU-side pipeline state:
    /// "Processing archives: 3/142 · GPU 12 batches · queue 45".
    /// </summary>
    private static string FormatGpuDetail(int doneArchives, int totalArchives, int flushedBatches, int queueDepth)
        => $"Processing archives: {doneArchives}/{totalArchives} · GPU {flushedBatches} batches · queue {queueDepth}";

    /// <summary>
    /// Cancellation-safe status reporter with a minimum 250 ms interval between reports —
    /// used for high-frequency detail lines (archive completions, queue snapshots) so a
    /// burst of archive events can't flood the UI thread. Coarse phase transitions are
    /// reported separately and stay immediate.
    /// </summary>
    private sealed class ThrottledStatusReporter
    {
        private static readonly long MinIntervalTicks = 250 * TimeSpan.TicksPerMillisecond;

        private readonly IProgress<ComicSearchStatus>? _status;
        private readonly CancellationToken _ct;
        private long _lastReportTicks; // 0 = never reported (tick counts are positive)

        public ThrottledStatusReporter(IProgress<ComicSearchStatus>? status, CancellationToken ct)
        {
            _status = status;
            _ct = ct;
        }

        public void Report(ComicSearchStatus update)
        {
            if (_status is null || _ct.IsCancellationRequested)
                return;

            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _lastReportTicks);
            if (now - last < MinIntervalTicks)
                return;

            // Only one caller wins the slot; losers skip this tick
            if (Interlocked.CompareExchange(ref _lastReportTicks, now, last) != last)
                return;

            _status.Report(update);
        }
    }

    internal static List<string> ScanArchivesRecursive(string directoryPath)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple,
            AttributesToSkip = 0
        };

        var files = new List<string>();
        foreach (var pattern in ArchivePatterns)
        {
            foreach (var file in Directory.EnumerateFiles(directoryPath, pattern, options))
            {
                files.Add(file);
            }
        }
        return files;
    }

    internal static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0;
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
            dot += a[i] * b[i];
        return dot;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private readonly record struct ImageWorkItem(
        string ArchivePath,
        int PageIndex,
        int PageCount,
        string CacheKey,
        DateTime LastModified,
        long FileSize,
        byte[] ImageData);
}