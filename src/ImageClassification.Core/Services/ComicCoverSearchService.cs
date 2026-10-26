using System.Collections.Concurrent;
using System.Diagnostics;
using ImageClassification.Core.Helpers;
using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageClassification.Core.Services;

/// <summary>
/// Scans CBZ/CBR archives and ranks their covers (or all pages) by CLIP
/// cosine similarity against a user query — either a text phrase (encoded with
/// the "a photo of" prefix) or an image (a loose file on disk, or a known page
/// of an archive whose cached ViT embedding is "copied" as the query). Image and
/// text queries live in the same embedding space, so one vector cache serves both.
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
    private readonly IClipImageEncoder _imageEncoder;
    private readonly IVectorStore _store;
    private readonly IBatchImageEncoder _batchEncoder;
    private readonly ILogger<ComicCoverSearchService> _logger;

    public ComicCoverSearchService(
        IArchiveReader archiveReader,
        IClipTextEncoder textEncoder,
        IClipImageEncoder imageEncoder,
        IVectorStore store,
        IBatchImageEncoder batchEncoder,
        ILogger<ComicCoverSearchService> logger)
    {
        _archiveReader = archiveReader ?? throw new ArgumentNullException(nameof(archiveReader));
        _textEncoder = textEncoder ?? throw new ArgumentNullException(nameof(textEncoder));
        _imageEncoder = imageEncoder ?? throw new ArgumentNullException(nameof(imageEncoder));
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
        // Text query — same "a photo of" prefix as the image tab so both tabs
        // share one query/image embedding space.
        return SearchCoreAsync(directoryPath, allPages: false, status, ct,
            CreateTextQueryFactory(query, status),
            excludeArchivePath: null,
            queryDescription: $"text '{query}'");
    }

    /// <inheritdoc />
    public Task<List<ComicCoverResult>> SearchAllPagesAsync(
        string directoryPath,
        string query,
        IProgress<ComicSearchStatus>? status = null,
        CancellationToken ct = default)
    {
        return SearchCoreAsync(directoryPath, allPages: true, status, ct,
            CreateTextQueryFactory(query, status),
            excludeArchivePath: null,
            queryDescription: $"text '{query}'");
    }

    /// <inheritdoc />
    public Task<List<ComicCoverResult>> SearchByImageAsync(
        string directoryPath,
        string queryImagePath,
        IProgress<ComicSearchStatus>? status = null,
        CancellationToken ct = default)
    {
        return SearchByImageFileAsync(directoryPath, allPages: false, queryImagePath, status, ct);
    }

    /// <inheritdoc />
    public Task<List<ComicCoverResult>> SearchAllPagesByImageAsync(
        string directoryPath,
        string queryImagePath,
        IProgress<ComicSearchStatus>? status = null,
        CancellationToken ct = default)
    {
        return SearchByImageFileAsync(directoryPath, allPages: true, queryImagePath, status, ct);
    }

    /// <inheritdoc />
    public Task<List<ComicCoverResult>> SearchByArchivePageAsync(
        string directoryPath,
        string queryArchivePath,
        int queryPageIndex,
        IProgress<ComicSearchStatus>? status = null,
        CancellationToken ct = default)
    {
        return SearchByArchivePageAsync(directoryPath, allPages: false, queryArchivePath, queryPageIndex, status, ct);
    }

    /// <inheritdoc />
    public Task<List<ComicCoverResult>> SearchAllPagesByArchivePageAsync(
        string directoryPath,
        string queryArchivePath,
        int queryPageIndex,
        IProgress<ComicSearchStatus>? status = null,
        CancellationToken ct = default)
    {
        return SearchByArchivePageAsync(directoryPath, allPages: true, queryArchivePath, queryPageIndex, status, ct);
    }

    /// <summary>
    /// Loose-image query ("search by this file"): fails fast BEFORE the (potentially
    /// long) archive scan when the file is missing or undecodable — the encoder
    /// re-checks at the encode step anyway.
    /// </summary>
    private Task<List<ComicCoverResult>> SearchByImageFileAsync(
        string directoryPath, bool allPages, string queryImagePath,
        IProgress<ComicSearchStatus>? status, CancellationToken ct)
    {
        if (!File.Exists(queryImagePath))
        {
            _logger.LogWarning("[Query] Image query file not found: {Path}", queryImagePath);
            status?.Report(new ComicSearchStatus
            {
                Note = $"Query image not found: {queryImagePath}"
            });
            return Task.FromResult(new List<ComicCoverResult>());
        }

        try
        {
            using var probe = Image.Load<Rgb24>(queryImagePath); // decode-only pre-check
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Query] Image query could not be decoded: {Path}", queryImagePath);
            status?.Report(new ComicSearchStatus
            {
                Note = $"Query image could not be read/decoded: {queryImagePath}"
            });
            return Task.FromResult(new List<ComicCoverResult>());
        }

        return SearchCoreAsync(directoryPath, allPages, status, ct,
            CreateImageFileQueryFactory(queryImagePath, status),
            excludeArchivePath: queryImagePath, // no-op unless the "archive" is the file itself
            queryDescription: queryImagePath);
    }

    /// <summary>
    /// Known-archive-page query ("copy the tile's ViT hash"): the page vector is
    /// almost always taken from the vector store (no archive open, no inference).
    /// The query archive itself is excluded from the results.
    /// </summary>
    private Task<List<ComicCoverResult>> SearchByArchivePageAsync(
        string directoryPath, bool allPages, string queryArchivePath, int queryPageIndex,
        IProgress<ComicSearchStatus>? status, CancellationToken ct)
    {
        if (!File.Exists(queryArchivePath))
        {
            _logger.LogWarning("[Query] Query archive not found: {Path}", queryArchivePath);
            status?.Report(new ComicSearchStatus
            {
                Note = $"Query archive not found: {queryArchivePath}"
            });
            return Task.FromResult(new List<ComicCoverResult>());
        }

        return SearchCoreAsync(directoryPath, allPages, status, ct,
            CreateArchivePageQueryFactory(queryArchivePath, queryPageIndex, status),
            excludeArchivePath: queryArchivePath,
            queryDescription: $"{queryArchivePath} (page {queryPageIndex + 1})");
    }

    /// <summary>Text-phrase query factory: encodes with the shared "a photo of" prefix.</summary>
    private Func<Stopwatch, CancellationToken, Task<float[]>> CreateTextQueryFactory(
        string query, IProgress<ComicSearchStatus>? status)
    {
        return async (sw, ct) =>
        {
            ReportPhase(status, ct, "Encoding query text...");
            var t0 = sw.Elapsed;
            var embedding = await _textEncoder.EncodeTextAsync($"a photo of {query}", ct).ConfigureAwait(false);
            _logger.LogInformation("[Query] Text query encoded in {Ms}ms", (sw.Elapsed - t0).TotalMilliseconds);
            return embedding;
        };
    }

    /// <summary>Loose-image query factory: the caching encoder makes an already-searched file a plain store lookup.</summary>
    private Func<Stopwatch, CancellationToken, Task<float[]>> CreateImageFileQueryFactory(
        string imagePath, IProgress<ComicSearchStatus>? status)
    {
        return async (sw, ct) =>
        {
            ReportPhase(status, ct, "Encoding query image...");
            var t0 = sw.Elapsed;
            var embedding = await _imageEncoder.EncodeImageAsync(imagePath, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "[Query] Image query encoded in {Ms}ms: {Path}", (sw.Elapsed - t0).TotalMilliseconds, imagePath);
            return embedding;
        };
    }

    /// <inheritdoc />
    public Task<List<ComicCoverResult>> SearchByVectorAsync(
        string directoryPath,
        float[] queryVector,
        IProgress<ComicSearchStatus>? status = null,
        CancellationToken ct = default)
        => SearchByVectorCoreAsync(directoryPath, allPages: false, queryVector, status, ct);

    /// <inheritdoc />
    public Task<List<ComicCoverResult>> SearchAllPagesByVectorAsync(
        string directoryPath,
        float[] queryVector,
        IProgress<ComicSearchStatus>? status = null,
        CancellationToken ct = default)
        => SearchByVectorCoreAsync(directoryPath, allPages: true, queryVector, status, ct);

    /// <inheritdoc />
    public async Task<string?> GetPageHashAsync(string archivePath, int pageIndex, CancellationToken ct = default)
    {
        if (pageIndex < 0 || !File.Exists(archivePath))
            return null;

        // The store's one-time legacy migration must be done before the lookup can see
        // migrated rows (the search core performs the same wait before its probes).
        await _store.WhenReady.ConfigureAwait(false);

        try
        {
            // Store first (one SQL lookup for a page that was ever searched), archive
            // open + encode only on a cache miss — the same resolution a tile's
            // "Find similar by this page" uses.
            var embedding = await ResolveArchivePageQueryAsync(archivePath, pageIndex, ct).ConfigureAwait(false);
            return IsZeroVector(embedding) ? null : VectorCodec.ToBase64(embedding);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Hash] Could not resolve the page hash of {Path} (page {Page})",
                archivePath, pageIndex + 1);
            return null;
        }
    }

    /// <summary>
    /// Pasted-hash search: the user-provided vector is used as-is as the query
    /// (no encoding, no self-exclusion). An empty vector is a UI input mistake —
    /// report it before the (potentially long) archive scan. A dimension mismatch
    /// with the cached embeddings is caught by the core (see SearchCoreAsync).
    /// </summary>
    private Task<List<ComicCoverResult>> SearchByVectorCoreAsync(
        string directoryPath, bool allPages, float[]? queryVector,
        IProgress<ComicSearchStatus>? status, CancellationToken ct)
    {
        if (queryVector is null || queryVector.Length == 0)
        {
            status?.Report(new ComicSearchStatus
            {
                Note = "Query hash is empty — paste the base64 hash copied from a result tile (right-click → Copy hash)."
            });
            return Task.FromResult(new List<ComicCoverResult>());
        }

        return SearchCoreAsync(directoryPath, allPages, status, ct,
            CreateVectorQueryFactory(queryVector, status),
            excludeArchivePath: null,
            queryDescription: $"hash ({queryVector.Length} floats)",
            validateQueryDimension: true);
    }

    /// <summary>Known-archive-page query factory: store first, archive open + encode only on a miss.</summary>
    private Func<Stopwatch, CancellationToken, Task<float[]>> CreateArchivePageQueryFactory(
        string archivePath, int pageIndex, IProgress<ComicSearchStatus>? status)
    {
        return async (sw, ct) =>
        {
            ReportPhase(status, ct, "Loading query image (page hash from cache first)...");
            var t0 = sw.Elapsed;
            var embedding = await ResolveArchivePageQueryAsync(archivePath, pageIndex, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "[Query] Archive page query resolved in {Ms}ms: {Path} page {Page}",
                (sw.Elapsed - t0).TotalMilliseconds, archivePath, pageIndex);
            return embedding;
        };
    }

    /// <summary>Pasted-hash query factory: the vector is used as-is, nothing is encoded.</summary>
    private static Func<Stopwatch, CancellationToken, Task<float[]>> CreateVectorQueryFactory(
        float[] vector, IProgress<ComicSearchStatus>? status)
    {
        return (sw, ct) =>
        {
            ReportPhase(status, ct, $"Using query hash ({vector.Length} floats)...");
            return Task.FromResult(vector);
        };
    }

    /// <summary>
    /// Shared pipeline for all query kinds and modes. The only mode difference: covers
    /// mode processes page 0 of each archive, all-pages mode processes every page.
    /// <paramref name="queryFactory"/> produces the query embedding at the point the text
    /// query used to be encoded (after the archive scan), so the phase order on screen
    /// is identical for text and image queries. Results of <paramref name="excludeArchivePath"/>
    /// are dropped (the query archive itself would score ~100% against itself).
    /// </summary>
    private async Task<List<ComicCoverResult>> SearchCoreAsync(
        string directoryPath,
        bool allPages,
        IProgress<ComicSearchStatus>? status,
        CancellationToken ct,
        Func<Stopwatch, CancellationToken, Task<float[]>> queryFactory,
        string? excludeArchivePath,
        string queryDescription,
        bool validateQueryDimension = false)
    {
        var sw = Stopwatch.StartNew();
        var label = allPages ? "SearchAllPages" : "Search";
        _logger.LogInformation(
            "[{Label}] Started — query={Query} dir='{Dir}' vectorCache='{CachePath}'",
            label, queryDescription, directoryPath, _store.DatabasePath ?? "<in-memory>");

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

        // 2. Query embedding — produced by the caller's factory: a text phrase (encoded
        //    with the "a photo of" prefix) or an image (its cached ViT embedding —
        //    "copy the hash"). A failing query aborts the search with a note; a
        //    cancelled query propagates.
        ReportPhase(status, ct, "Preparing query...");
        t0 = sw.Elapsed;
        float[] queryEmbedding;
        try
        {
            queryEmbedding = await queryFactory(sw, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Label}] Query failed: {Description}", label, queryDescription);
            if (!ct.IsCancellationRequested)
            {
                ReportPhase(status, ct, "Query failed — no results");
                status?.Report(new ComicSearchStatus
                {
                    Note = $"Query could not be processed: {queryDescription}"
                });
            }
            return new List<ComicCoverResult>();
        }
        Debug.Assert(queryEmbedding is { Length: > 0 }, "query embedding must not be empty");
        _logger.LogInformation("[{Label}] Query ready in {Ms}ms", label, (sw.Elapsed - t0).TotalMilliseconds);

        // 2.2. A PASTED hash can come from a different model (different dimension).
        // Scoring such a vector would trip the dimension assert (Debug) or silently
        // produce garbage (Release) — validate against a sample of the cached rows
        // (one SQL lookup on the first scanned archive) and bail out with a note.
        // Text/image queries always match the cache's model, so this runs only for
        // the vector (hash) variant. When no sample row exists (a directory never
        // searched) the check is skipped — there is nothing to compare against.
        if (validateQueryDimension)
        {
            var sampleLength = await ProbeSampleDimensionAsync(archiveFiles[0], allPages).ConfigureAwait(false);
            if (sampleLength is not null && sampleLength != queryEmbedding.Length)
            {
                _logger.LogWarning("[{Label}] Query hash dimension {QueryDim} != cached dimension {SampleDim}",
                    label, queryEmbedding.Length, sampleLength);
                if (!ct.IsCancellationRequested)
                {
                    ReportPhase(status, ct, "Query failed — no results");
                    status?.Report(new ComicSearchStatus
                    {
                        Note = $"Query hash has {queryEmbedding.Length} floats but the cached embeddings have {sampleLength} — it was copied from a different model."
                    });
                }
                return new List<ComicCoverResult>();
            }
        }

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
                    // The query archive itself is excluded — its own cover/page would
                    // score ~100% and top the list (image queries only; text has none).
                    if (excludeArchivePath is not null && IsSameArchivePath(r.ArchivePath, excludeArchivePath))
                        return;
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
    /// The dimension of whatever cached embedding row the archive has (all-pages row
    /// first, then cover row) — the sample the pasted-hash dimension check compares
    /// against. Null when the archive has no cached rows at all.
    /// </summary>
    private async Task<int?> ProbeSampleDimensionAsync(string archivePath, bool allPages)
    {
        DateTime lastModified;
        long fileSize;
        try
        {
            var fi = new FileInfo(archivePath);
            lastModified = fi.LastWriteTimeUtc;
            fileSize = fi.Length;
        }
        catch
        {
            lastModified = DateTime.MinValue;
            fileSize = 0;
        }

        if (allPages)
        {
            var archived = await GetArchiveCachedAsync(archivePath, lastModified, fileSize);
            if (archived is not null)
            {
                foreach (var page in archived.Value.Pages)
                    if (page is not null && !IsZeroVector(page))
                        return page.Length;
            }

            var cover = await GetCoverCachedAsync(archivePath, lastModified, fileSize);
            if (cover is not null && !IsZeroVector(cover))
                return cover.Length;
            return null;
        }

        var coverHit = await GetCoverCachedAsync(archivePath, lastModified, fileSize);
        if (coverHit is not null && !IsZeroVector(coverHit))
            return coverHit.Length;

        var archivedHit = await GetArchiveCachedAsync(archivePath, lastModified, fileSize);
        if (archivedHit is not null)
        {
            foreach (var page in archivedHit.Value.Pages)
                if (page is not null && !IsZeroVector(page))
                    return page.Length;
        }
        return null;
    }

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
        var score = VectorMath.CosineSimilarity(embedding, queryEmbedding);
        // A NaN score would silently sink the result to the bottom of the ranked list.
        Debug.Assert(!float.IsNaN(score), "similarity score must not be NaN");
        return new ComicCoverResult
        {
            ArchivePath = archivePath,
            ArchiveFileName = Path.GetFileName(archivePath),
            SimilarityScore = score,
            PageIndex = pageIndex,
            PageCount = pageCount
        };
    }

    /// <summary>
    /// Resolves the query embedding of a KNOWN archive page: fresh store rows win —
    /// the all-pages row (every page) or the cover row (page 0) is a single SQL
    /// lookup, no archive open and no inference, i.e. a literal "copy the ViT hash".
    /// On a miss the page is extracted from the archive and encoded; a page-0 result
    /// is persisted as the cover row so the next lookup hits.
    /// </summary>
    private async Task<float[]> ResolveArchivePageQueryAsync(string archivePath, int pageIndex, CancellationToken ct)
    {
        DateTime lastModified;
        long fileSize;
        try
        {
            var fi = new FileInfo(archivePath);
            lastModified = fi.LastWriteTimeUtc;
            fileSize = fi.Length;
        }
        catch
        {
            lastModified = DateTime.MinValue; // stat raced a deletion — treat as a cache miss
            fileSize = 0;
        }

        if (pageIndex >= 0)
        {
            // All-pages rows carry every page — a tile shown by an all-pages search
            // almost always has its row already.
            var archived = await GetArchiveCachedAsync(archivePath, lastModified, fileSize);
            if (archived is not null)
            {
                var (_, pages) = archived.Value;
                if (pageIndex < pages.Length && pages[pageIndex] is { } pageVec && !IsZeroVector(pageVec))
                    return pageVec;
            }
        }

        if (pageIndex == 0)
        {
            var cover = await GetCoverCachedAsync(archivePath, lastModified, fileSize);
            if (cover is not null && !IsZeroVector(cover))
                return cover;
        }

        // Cache miss: open the archive, extract the page, encode it.
        using var session = await _archiveReader.OpenSessionAsync(archivePath).ConfigureAwait(false);
        if (pageIndex < 0 || pageIndex >= session.ImageEntries.Count)
            throw new InvalidOperationException(
                $"Page {pageIndex + 1} is out of range in {Path.GetFileName(archivePath)}");

        var bytes = await session.ExtractToMemoryAsync(pageIndex).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0)
            throw new InvalidOperationException(
                $"Page {pageIndex + 1} of {Path.GetFileName(archivePath)} extracted empty");

        var embeddings = await _batchEncoder
            .EncodeImagesFromMemoryBatchAsync(new List<(int, byte[])> { (0, bytes) }, null, ct)
            .ConfigureAwait(false);
        var embedding = embeddings.Length > 0 ? embeddings[0] : [];
        if (embedding is null || IsZeroVector(embedding))
            throw new InvalidOperationException(
                $"Page {pageIndex + 1} of {Path.GetFileName(archivePath)} could not be decoded");

        // Best-effort: persist a page-0 embedding as the cover row so the next
        // lookup of the same page is a cache hit.
        if (pageIndex == 0)
        {
            try
            {
                await _store.SaveCoverAsync(ModelName, archivePath, lastModified, fileSize, embedding)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Caching only — the query embedding above is already computed.
            }
        }

        return embedding;
    }

    /// <summary>
    /// Zero-filled embedding = the encoder could not read/decode the input (the
    /// convention IBatchImageEncoder documents); zero vectors must never be used as
    /// a query or saved as a cache row.
    /// </summary>
    private static bool IsZeroVector(float[]? embedding)
    {
        if (embedding is null)
            return true;
        for (var i = 0; i < embedding.Length; i++)
            if (embedding[i] != 0f)
                return false;
        return true;
    }

    /// <summary>
    /// Path equality for the query-archive exclusion: full-path normalized and
    /// case-insensitive (directory enumeration can return a different case format
    /// than the caller's path). GetFullPath failures fall back to a direct comparison
    /// rather than breaking the search.
    /// </summary>
    private static bool IsSameArchivePath(string a, string? b)
    {
        if (b is null)
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
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