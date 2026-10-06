using System.Collections.Concurrent;
using ImageClassification.Core.Helpers;
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
    // PDFs (scanned single-file comics) join the archive formats; they are handled by the
    // ImageClassification.Pdf container handler (registered via AddPdf() in the DI composition root).
    private static readonly string[] ArchivePatterns = { "*.cbz", "*.cbr", "*.cb7", "*.cbt", "*.pdf" };

    private readonly IArchiveReader _archiveReader;
    private readonly IClipTextEncoder _textEncoder;
    private readonly IVectorStore _store;
    private readonly IBatchImageEncoder _batchEncoder;
    private readonly ILogger<ComicCoverSearchService> _logger;

    public ComicCoverSearchService(
        IArchiveReader archiveReader,
        IClipTextEncoder textEncoder,
        IVectorStore store,
        IBatchImageEncoder batchEncoder,
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

        // 2.5. The store's one-time legacy migration runs on a background thread at
        // startup. It has almost always finished by the time a search starts; if not,
        // wait for it here and say so explicitly instead of stalling at 0/N.
        var migWait = sw.Elapsed;
        await _store.WhenReady;
        var migMs = (sw.Elapsed - migWait).TotalMilliseconds;
        if (migMs > 1000)
        {
            _logger.LogInformation("[{Label}] Waited {Ms} ms for the vector-cache migration to finish",
                label, migMs);
            ReportPhase(status, ct, "Vector cache: legacy migration finished — resuming...");
        }

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
        int archivesSaved = 0;
        int saveFailures = 0;

        // Per-archive save buffers, touched by the consumer thread only. An archive is
        // saved (one packed "archive_embeddings" row / one "cover_embeddings" row) only
        // when EVERY page of it ended up in the buffer — that row then doubles as the
        // "fully cached" marker for the next run. Cancelled or partially failed
        // archives keep no row and are re-encoded on the next search.
        var saveBuffers = new Dictionary<string, ArchiveSaveBuffer>(StringComparer.Ordinal);

        // Throttled (250 ms) reporter for high-frequency detail lines:
        // per-archive completion and GPU-queue snapshots shown verbatim in the UI.
        var detailStatus = new ThrottledStatusReporter<ComicSearchStatus>(status, ct);

        // Independent 250 ms throttle for the "currently processing file" reports: they
        // ride the RAW status channel (bypassing the detail slot), so a burst of archive
        // starts can't starve the GPU-queue detail lines — and the UI sees at most
        // ~4 file labels per second.
        const long fileReportIntervalTicks = 250 * TimeSpan.TicksPerMillisecond;
        long lastFileReportTicks = 0;

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

                            // "Currently processing" label: the UI shows the most recently
                            // started archive (self-throttled to 250 ms, own slot).
                            long nowTicks = DateTime.UtcNow.Ticks;
                            long lastFile = Interlocked.Read(ref lastFileReportTicks);
                            if (nowTicks - lastFile >= fileReportIntervalTicks
                                && Interlocked.CompareExchange(ref lastFileReportTicks, nowTicks, lastFile) == lastFile)
                            {
                                status?.Report(new ComicSearchStatus { CurrentFile = path });
                            }

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
                            // A fresh "archive_embeddings" row carries the WHOLE page array
                            // plus its page count, so it doubles as the "fully cached"
                            // marker: even a fresh process skips the archive open entirely.

                            int knownPageCount = 0;

                            if (allPages)
                            {
                                var cached = await GetArchiveCachedAsync(path, lastModified, fileSize);
                                if (cached is not null)
                                {
                                    var (pageTotal, pages) = cached.Value;
                                    knownPageCount = pageTotal;
                                    for (int p = 0; p < pageTotal; p++)
                                    {
                                        token.ThrowIfCancellationRequested();
                                        Interlocked.Increment(ref cacheHits);
                                        onResult(ToResult(path, p, pageTotal, pages[p], queryEmbedding));
                                    }

                                    _logger.LogInformation(
                                        "[Pipeline] {Name}: all {Pages} page(s) cached — archive not opened",
                                        Path.GetFileName(path), pageTotal);

                                    ReportArchiveDone();
                                    return;
                                }
                                // (per-page misses are counted in the slow-path loop below)
                            }
                            else
                            {
                                // Covers mode: a single cache probe for page 0.
                                var coverHit = await GetCoverCachedAsync(path, lastModified, fileSize);
                                if (coverHit is not null)
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
                                knownPageCount = pageCount;

                                // The cache row is all-or-nothing per archive (a fresh row
                                // means every page was cached and was taken by the fast
                                // path above), so on the slow path EVERY page is a miss:
                                // multi-page archives are extracted in ONE bulk pass —
                                // for solid 7z archives per-entry extraction re-decodes the
                                // whole solid prefix for every entry (O(N²)), while a single
                                // full pass is O(N). Covers (pageEnd == 1) extract the one
                                // page directly.
                                byte[]?[]? bulk = pageEnd > 1
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

                                int archiveMisses = 0;

                                for (int p = 0; p < pageEnd; p++)
                                {
                                    token.ThrowIfCancellationRequested();

                                    // Take bytes from the bulk pass (when used) or extract
                                    // from the already-open session handle (per-page misses
                                    // are aggregated into the per-archive and final
                                    // "Cache summary" log lines — no per-page log flood)
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

                                    var workItem = new ImageWorkItem(path, p, pageCount, allPages, lastModified, fileSize, bytes);
                                    // Blocking add: backpressure on the bounded queue, respects the token
                                    queue.Add(workItem, token);
                                    Interlocked.Increment(ref enqueued);
                                }

                                _logger.LogInformation(
                                    "[Pipeline] {Name}: {Total} page(s) to encode (no fresh cache row)",
                                    Path.GetFileName(path), pageEnd);
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
                                    knownPageCount);
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

                // Accumulate the page in the archive's save buffer; when the last page
                // arrives, the whole archive is written as ONE row (which doubles as its
                // "fully cached" marker). The consumer is the only thread here, so the
                // plain dictionary needs no locking.
                if (!saveBuffers.TryGetValue(work.ArchivePath, out var buffer))
                {
                    buffer = new ArchiveSaveBuffer(work.AllPages, work.AllPages ? work.PageCount : 1,
                        work.LastModified, work.FileSize);
                    saveBuffers[work.ArchivePath] = buffer;
                }
                if (!buffer.Add(work.PageIndex, embedding))
                    continue;

                saveBuffers.Remove(work.ArchivePath);
                try
                {
                    await buffer.SaveAsync(_store, ModelName, work.ArchivePath).ConfigureAwait(false);
                    Interlocked.Increment(ref archivesSaved);
                }
                catch
                {
                    // Best-effort cache save — the search result is still complete; the
                    // archive simply gets no "fully cached" row and is re-encoded next run.
                    Interlocked.Increment(ref saveFailures);
                    _logger.LogWarning(
                        "[Pipeline] Saving archive row failed for {Path} — it will be re-encoded on the next search",
                        work.ArchivePath);
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

        // The "fully cached" rows of the archives opened in this run were already
        // persisted by the consumer the moment each archive's last page was encoded —
        // an archive row exists only when EVERY page of the archive is in the cache.
        _logger.LogInformation(
            "[Pipeline] Completed: archives={Archives}, enqueued={Enqueued}, cacheHits={Hits}, failedExtractions={Failed}, flushedBatches={Batches}, archivesSaved={ArchivesSaved}, saveFailures={SaveFailures}",
            archiveFiles.Count, enqueued, cacheHits, failedExtractions, flushedBatches, archivesSaved, saveFailures);

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
    public async Task<byte[]?> ExtractPageAsync(string archivePath, int pageIndex)
    {
        using var session = await _archiveReader.OpenSessionAsync(archivePath).ConfigureAwait(false);
        return await session.ExtractToMemoryAsync(pageIndex).ConfigureAwait(false);
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
    /// Fresh "archive_embeddings" row of the archive — the whole page array plus its
    /// page count — or null when the row is missing / stale / the store fails. A
    /// throwing store (locked/corrupt DB) must not break the search: the archive is
    /// opened and re-encoded as usual instead of masquerading as "cache not working".
    /// </summary>
    private async Task<(int PageCount, float[][] Pages)?> GetArchiveCachedAsync(
        string archivePath, DateTime lastModifiedUtc, long fileSize)
    {
        try
        {
            return await _store
                .GetArchiveAsync(ModelName, archivePath, lastModifiedUtc, fileSize)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[Pipeline] Vector store archive probe failed — {Path} treated as a miss", archivePath);
            return null;
        }
    }

    /// <summary>Fresh cover vector, or null on miss / stale / store failure (see above).</summary>
    private async Task<float[]?> GetCoverCachedAsync(
        string archivePath, DateTime lastModifiedUtc, long fileSize)
    {
        try
        {
            return await _store
                .GetCoverAsync(ModelName, archivePath, lastModifiedUtc, fileSize)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[Pipeline] Vector store cover probe failed — {Path} treated as a miss", archivePath);
            return null;
        }
    }

    private static ComicCoverResult ToResult(
        string archivePath, int pageIndex, int pageCount, float[] embedding, float[] queryEmbedding)
    {
        return new ComicCoverResult
        {
            ArchivePath = archivePath,
            ArchiveFileName = Path.GetFileName(archivePath),
            SimilarityScore = VectorMath.CosineSimilarity(embedding, queryEmbedding),
            PageIndex = pageIndex,
            PageCount = pageCount
        };
    }

    /// <summary>
    /// Builds the full status line (shown verbatim by the UI — no trailing "...")
    /// with the archive counter and the GPU-side pipeline state:
    /// "Processing archives: 3/142 · GPU 12 batches · queue 45".
    /// </summary>
    private static string FormatGpuDetail(int doneArchives, int totalArchives, int flushedBatches, int queueDepth)
        => $"Processing archives: {doneArchives}/{totalArchives} · GPU {flushedBatches} batches · queue {queueDepth}";

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

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private readonly record struct ImageWorkItem(
        string ArchivePath,
        int PageIndex,
        int PageCount,
        bool AllPages,
        DateTime LastModified,
        long FileSize,
        byte[] ImageData);

    /// <summary>
    /// Accumulates one archive's encoded pages on the consumer thread. Only an archive
    /// where EVERY expected page arrived produces a cache row (all-pages: one packed
    /// "archive_embeddings" row; covers: one "cover_embeddings" row) — that row doubles
    /// as the "fully cached" marker for the next run.
    /// </summary>
    private sealed class ArchiveSaveBuffer
    {
        private readonly bool _allPages;
        private readonly DateTime _lastModifiedUtc;
        private readonly long _fileSize;
        private readonly float[]?[] _slots;
        private int _pending;

        public ArchiveSaveBuffer(bool allPages, int expectedPages, DateTime lastModifiedUtc, long fileSize)
        {
            _allPages = allPages;
            _lastModifiedUtc = lastModifiedUtc;
            _fileSize = fileSize;
            _slots = new float[expectedPages > 0 ? expectedPages : 1][];
            _pending = _slots.Length;
        }

        /// <summary>
        /// Stores one encoded page. Returns true when this was the last pending page,
        /// i.e. the whole archive is ready to be written as a single cache row.
        /// </summary>
        public bool Add(int pageIndex, float[] embedding)
        {
            if (pageIndex < 0 || pageIndex >= _slots.Length || _slots[pageIndex] is not null)
                return _pending == 0; // unexpected or duplicate page — ignore
            _slots[pageIndex] = embedding;
            _pending--;
            return _pending == 0;
        }

        public async Task SaveAsync(IVectorStore store, string modelName, string archivePath)
        {
            if (_allPages)
            {
                var pages = new float[_slots.Length][];
                for (var i = 0; i < _slots.Length; i++)
                    pages[i] = _slots[i]!;
                await store.SaveArchiveAsync(modelName, archivePath, pages.Length,
                    _lastModifiedUtc, _fileSize, pages);
            }
            else
            {
                await store.SaveCoverAsync(modelName, archivePath, _lastModifiedUtc, _fileSize, _slots[0]!);
            }
        }
    }
}