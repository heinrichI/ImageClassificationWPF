using System.Collections.Concurrent;
using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using Microsoft.Extensions.Logging;

namespace ImageClassification.Core.Services;

/// <summary>
/// Scans CBZ/CBR archives, extracts covers or all pages, and ranks them
/// by CLIP cosine similarity against a user text query.
/// Uses BatchImageEncoder for batched GPU inference and IVectorStore for caching.
/// </summary>
internal sealed class ComicCoverSearchService : IComicCoverSearchService
{
    private const string ModelName = "ClipViTB32";
    private static readonly string[] ArchivePatterns = { "*.cbz", "*.cbr", "*.cb7", "*.cbt" };

    private readonly IArchiveReader _archiveReader;
    private readonly IClipTextEncoder _textEncoder;
    private readonly IVectorStore _store;
    private readonly BatchImageEncoder _batchEncoder;
    private readonly ILogger<ComicCoverSearchService> _logger;

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

    /// <inheritdoc />
    public Task LoadModelAsync(string clipOnnxPath)
    {
        // No-op — model loading is delegated to BatchImageEncoder (lazy)
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<List<ComicCoverResult>> SearchAsync(
        string directoryPath,
        string query,
        IProgress<(int Current, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("[Search] Started — query='{Query}' dir='{Dir}'", query, directoryPath);

        if (!Directory.Exists(directoryPath))
        {
            _logger.LogWarning("[Search] Directory not found: {Dir}", directoryPath);
            return new List<ComicCoverResult>();
        }

        // 1. Scan archives
        var t0 = sw.Elapsed;
        var archiveFiles = ScanArchivesRecursive(directoryPath);
        _logger.LogInformation("[Search] Scan: {Count} archives found in {Ms}ms",
            archiveFiles.Count, (sw.Elapsed - t0).TotalMilliseconds);

        if (archiveFiles.Count == 0)
            return new List<ComicCoverResult>();

        // 2. Encode the text query once
        t0 = sw.Elapsed;
        var textQuery = $"a photo of {query}";
        var queryEmbedding = await _textEncoder.EncodeTextAsync(textQuery, ct).ConfigureAwait(false);
        _logger.LogInformation("[Search] Text encoding: {Ms}ms", (sw.Elapsed - t0).TotalMilliseconds);

        // 3. Batch-check cache — one DB round-trip
        t0 = sw.Elapsed;
        var entries = BuildMetaEntries(archiveFiles);
        var cacheResults = await BatchGetCacheAsync(entries).ConfigureAwait(false);
        int cacheHits = cacheResults.Count(kvp => kvp.Value is not null);
        _logger.LogInformation("[Search] Cache check: {Hits}/{Total} hits in {Ms}ms",
            cacheHits, archiveFiles.Count, (sw.Elapsed - t0).TotalMilliseconds);

        // 4. Identify cache misses
        var misses = new List<(int OriginalIndex, string ArchivePath, int PageIndex, string CacheKey, DateTime LastModified, long FileSize)>();
        var results = new (string ArchivePath, float[]? Embedding)[archiveFiles.Count];

        for (int i = 0; i < archiveFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var archivePath = archiveFiles[i];
            cacheResults.TryGetValue(archivePath, out var cached);
            var (_, lm, fs) = entries[i];

            results[i] = (archivePath, cached);

            if (cached is null)
            {
                misses.Add((i, archivePath, 0, archivePath, lm, fs));
            }
        }

        _logger.LogInformation("[Search] Misses: {Misses}, cache hits: {Hits}",
            misses.Count, archiveFiles.Count - misses.Count);

        _logger.LogInformation("[Search] Reporting initial progress: {Current}/{Total}", cacheHits, archiveFiles.Count);
        progress?.Report((cacheHits, archiveFiles.Count));

        if (misses.Count > 0)
        {
            t0 = sw.Elapsed;
            await RunProducerConsumerAsync(misses, results, archiveFiles.Count, cacheHits, progress, ct).ConfigureAwait(false);
            _logger.LogInformation("[Search] Producer/consumer pipeline completed in {Ms}ms",
                (sw.Elapsed - t0).TotalMilliseconds);
        }
        else
        {
            _logger.LogInformation("[Search] All archives were resolved from cache, extraction/inference skipped");
        }

        // 5. Score + sort
        var output = new List<ComicCoverResult>(archiveFiles.Count);
        for (int i = 0; i < archiveFiles.Count; i++)
        {
            var (archivePath, embedding) = results[i];
            if (embedding is null) continue;

            output.Add(new ComicCoverResult
            {
                ArchivePath = archivePath,
                ArchiveFileName = Path.GetFileName(archivePath),
                CoverImagePath = string.Empty,
                SimilarityScore = CosineSimilarity(embedding, queryEmbedding),
                PageIndex = 0,
                PageCount = 1
            });
        }

        _logger.LogInformation("[Search] Reporting final progress: {Current}/{Total}", archiveFiles.Count, archiveFiles.Count);
        progress?.Report((archiveFiles.Count, archiveFiles.Count));
        _logger.LogInformation("[Search] Done: {Results} results, top score={TopScore:F4}, total={TotalMs}ms",
            output.Count,
            output.Count > 0 ? output.Max(x => x.SimilarityScore) : 0f,
            sw.ElapsedMilliseconds);

        return output.OrderByDescending(x => x.SimilarityScore).ToList();
    }

    /// <inheritdoc />
    public async Task<List<ComicCoverResult>> SearchAllPagesAsync(
        string directoryPath,
        string query,
        IProgress<(int Current, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("[SearchAllPages] Started — query='{Query}' dir='{Dir}'", query, directoryPath);

        if (!Directory.Exists(directoryPath))
        {
            _logger.LogWarning("[SearchAllPages] Directory not found: {Dir}", directoryPath);
            return new List<ComicCoverResult>();
        }

        // 1. Scan archives
        var t0 = sw.Elapsed;
        var archiveFiles = ScanArchivesRecursive(directoryPath);
        _logger.LogInformation("[SearchAllPages] Scan: {Count} archives in {Ms}ms",
            archiveFiles.Count, (sw.Elapsed - t0).TotalMilliseconds);

        if (archiveFiles.Count == 0)
            return new List<ComicCoverResult>();

        // 2. Encode the text query once
        t0 = sw.Elapsed;
        var textQuery = $"a photo of {query}";
        var queryEmbedding = await _textEncoder.EncodeTextAsync(textQuery, ct).ConfigureAwait(false);
        _logger.LogInformation("[SearchAllPages] Text encoding: {Ms}ms", (sw.Elapsed - t0).TotalMilliseconds);

        // 3. Get page counts for all archives
        t0 = sw.Elapsed;
        var archivePageCounts = new int[archiveFiles.Count];
        int totalPages = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, archiveFiles.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
            async (i, token) =>
            {
                try
                {
                    int count = await _archiveReader.GetImageCountAsync(archiveFiles[i]).ConfigureAwait(false);
                    archivePageCounts[i] = count;
                    Interlocked.Add(ref totalPages, count);
                }
                catch
                {
                    archivePageCounts[i] = 0;
                }
            }).ConfigureAwait(false);

        _logger.LogInformation("[SearchAllPages] Page count: {TotalPages} pages across {Archives} archives in {Ms}ms",
            totalPages, archiveFiles.Count, (sw.Elapsed - t0).TotalMilliseconds);

        if (totalPages == 0)
            return new List<ComicCoverResult>();

        // 4. Build flat list of all (archiveIdx, pageIdx, cacheKey) entries
        var allPages = new List<(int ArchiveIdx, int PageIdx, string CacheKey, DateTime LastModified, long FileSize)>();
        var metaLookup = BuildMetaLookup(archiveFiles);

        for (int i = 0; i < archiveFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var path = archiveFiles[i];
            metaLookup.TryGetValue(path, out var meta);

            for (int p = 0; p < archivePageCounts[i]; p++)
            {
                allPages.Add((i, p, $"{path}|page={p}", meta.LastModified, meta.FileSize));
            }
        }

        // 5. Batch-check cache for all pages
        t0 = sw.Elapsed;
        var cacheEntries = allPages
            .Select(x => (x.CacheKey, x.LastModified, x.FileSize))
            .ToList();
        var cacheResults = await BatchGetCacheAsync(cacheEntries).ConfigureAwait(false);

        // 6. Identify cache misses and process through producer/consumer pipeline
        var misses = new List<(int OriginalIndex, string ArchivePath, int PageIndex, string CacheKey, DateTime LastModified, long FileSize)>();
        var results = new (string ArchivePath, float[]? Embedding)[allPages.Count];

        for (int i = 0; i < allPages.Count; i++)
        {
            var (archiveIdx, pageIdx, cacheKey, lm, fs) = allPages[i];
            var archivePath = archiveFiles[archiveIdx];

            cacheResults.TryGetValue(cacheKey, out var cached);
            results[i] = (archivePath, cached);

            if (cached is null)
            {
                misses.Add((i, archivePath, pageIdx, cacheKey, lm, fs));
            }
        }

        int pageHits = allPages.Count - misses.Count;
        _logger.LogInformation("[SearchAllPages] Cache check: {Hits}/{Total} page hits in {Ms}ms",
            pageHits, allPages.Count, (sw.Elapsed - t0).TotalMilliseconds);

        _logger.LogInformation("[SearchAllPages] Page misses: {Misses}, page hits: {Hits}",
            misses.Count, pageHits);

        _logger.LogInformation("[SearchAllPages] Reporting initial progress: {Current}/{Total}", pageHits, totalPages);
        progress?.Report((pageHits, totalPages));

        if (misses.Count > 0)
        {
            t0 = sw.Elapsed;
            await RunProducerConsumerAsync(misses, results, totalPages, pageHits, progress, ct).ConfigureAwait(false);
            _logger.LogInformation("[SearchAllPages] Producer/consumer pipeline completed in {Ms}ms",
                (sw.Elapsed - t0).TotalMilliseconds);
        }
        else
        {
            _logger.LogInformation("[SearchAllPages] All pages were resolved from cache, extraction/inference skipped");
        }

        // 7. Score + sort
        var output = new List<ComicCoverResult>(allPages.Count);
        for (int i = 0; i < allPages.Count; i++)
        {
            var embedding = results[i].Embedding;
            if (embedding is null) continue;

            var (archiveIdx, pageIdx, _, _, _) = allPages[i];
            var archivePath = archiveFiles[archiveIdx];

            output.Add(new ComicCoverResult
            {
                ArchivePath = archivePath,
                ArchiveFileName = Path.GetFileName(archivePath),
                CoverImagePath = string.Empty,
                SimilarityScore = CosineSimilarity(embedding, queryEmbedding),
                PageIndex = pageIdx,
                PageCount = archivePageCounts[archiveIdx]
            });
        }

        _logger.LogInformation("[SearchAllPages] Reporting final progress: {Current}/{Total}", totalPages, totalPages);
        progress?.Report((totalPages, totalPages));
        _logger.LogInformation("[SearchAllPages] Done: {Results} results, top score={TopScore:F4}, total={TotalMs}ms",
            output.Count,
            output.Count > 0 ? output.Max(x => x.SimilarityScore) : 0f,
            sw.ElapsedMilliseconds);

        return output.OrderByDescending(x => x.SimilarityScore).ToList();
    }

    /// <inheritdoc />
    public async Task<string?> ExtractCoverAsync(string archivePath)
    {
        return await _archiveReader.ExtractImageByIndexAsync(archivePath, 0)
            .ConfigureAwait(false);
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

    private async Task RunProducerConsumerAsync(
        List<(int OriginalIndex, string ArchivePath, int PageIndex, string CacheKey, DateTime LastModified, long FileSize)> misses,
        (string ArchivePath, float[]? Embedding)[] results,
        int totalForProgress,
        int initialDone,
        IProgress<(int Current, int Total)>? progress,
        CancellationToken ct)
    {
        int queueCapacity = Math.Max(1, _batchEncoder.BatchSize * 2);
        using var queue = new BlockingCollection<ImageWorkItem>(queueCapacity);

        int totalMisses = misses.Count;
        int processed = 0;
        int extractedFailed = 0;
        int enqueued = 0;
        int dequeued = 0;
        int batchesFlushed = 0;

        _logger.LogInformation(
            "[Pipeline] Start: misses={Misses}, queueCapacity={Capacity}, batchSize={BatchSize}, progressTotal={ProgressTotal}, initialDone={InitialDone}, hasProgressReporter={HasProgress}",
            totalMisses, queueCapacity, _batchEncoder.BatchSize, totalForProgress, initialDone, progress is not null);

        void ReportProgressIncrement()
        {
            int current = Interlocked.Increment(ref processed);

            if (totalForProgress > 0)
            {
                int currentForUi = Math.Min(initialDone + current, totalForProgress);
                _logger.LogDebug("[Pipeline] progress.Report({Current}/{Total})", currentForUi, totalForProgress);
                progress?.Report((currentForUi, totalForProgress));
            }

            if (current == 1 || current == totalMisses || current % 25 == 0)
            {
                _logger.LogInformation(
                    "[Pipeline] Progress: processed={Processed}/{Misses}, enqueued={Enqueued}, dequeued={Dequeued}, failedExtractions={Failed}",
                    current, totalMisses, Volatile.Read(ref enqueued), Volatile.Read(ref dequeued), Volatile.Read(ref extractedFailed));
            }
        }

        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (var miss in misses)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var bytes = await _archiveReader
                            .ExtractImageToMemoryAsync(miss.ArchivePath, miss.PageIndex)
                            .ConfigureAwait(false);

                        if (bytes is null || bytes.Length == 0)
                        {
                            Interlocked.Increment(ref extractedFailed);
                            _logger.LogWarning("[Pipeline] Empty image bytes extracted: {Path} page {PageIndex}",
                                miss.ArchivePath, miss.PageIndex);
                            results[miss.OriginalIndex] = (miss.ArchivePath, null);
                            ReportProgressIncrement();
                            continue;
                        }

                        var workItem = new ImageWorkItem(
                            miss.OriginalIndex,
                            miss.ArchivePath,
                            miss.PageIndex,
                            miss.CacheKey,
                            miss.LastModified,
                            miss.FileSize,
                            bytes);

                        queue.Add(workItem, ct);
                        Interlocked.Increment(ref enqueued);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref extractedFailed);
                        _logger.LogWarning(ex, "Image extraction failed: {Path} page {PageIndex}",
                            miss.ArchivePath, miss.PageIndex);
                        results[miss.OriginalIndex] = (miss.ArchivePath, null);
                        ReportProgressIncrement();
                    }
                }
            }
            finally
            {
                queue.CompleteAdding();
                _logger.LogInformation("[Pipeline] Producer completed: enqueued={Enqueued}, failedExtractions={Failed}",
                    Volatile.Read(ref enqueued), Volatile.Read(ref extractedFailed));
            }
        }, ct);

        var batch = new List<ImageWorkItem>(_batchEncoder.BatchSize);

        async Task FlushBatchAsync()
        {
            if (batch.Count == 0)
                return;

            List<(int OriginalIndex, byte[] Data)> payload = batch
                .Select(w => (w.OriginalIndex, w.ImageData))
                .ToList();

            int flushNumber = Interlocked.Increment(ref batchesFlushed);
            _logger.LogInformation("[Pipeline] Flushing batch #{BatchNumber} with {Count} images", flushNumber, batch.Count);

            var embeddings = await _batchEncoder
                .EncodeImagesFromMemoryBatchAsync(payload, progress: null, ct)
                .ConfigureAwait(false);

            for (int i = 0; i < batch.Count; i++)
            {
                var work = batch[i];
                var embedding = embeddings[i];

                results[work.OriginalIndex] = (work.ArchivePath, embedding);

                try
                {
                    await _store
                        .SaveAsync(ModelName, work.CacheKey, work.LastModified, work.FileSize, embedding)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Best effort cache save
                }

                ReportProgressIncrement();
            }

            batch.Clear();
        }

        foreach (var item in queue.GetConsumingEnumerable(ct))
        {
            ct.ThrowIfCancellationRequested();

            batch.Add(item);
            Interlocked.Increment(ref dequeued);
            if (batch.Count >= _batchEncoder.BatchSize)
            {
                await FlushBatchAsync().ConfigureAwait(false);
            }
        }

        await FlushBatchAsync().ConfigureAwait(false);
        await producer.ConfigureAwait(false);

        _logger.LogInformation(
            "[Pipeline] Completed: misses={Misses}, processed={Processed}, enqueued={Enqueued}, dequeued={Dequeued}, failedExtractions={Failed}, flushedBatches={Batches}",
            totalMisses, processed, enqueued, dequeued, extractedFailed, batchesFlushed);
    }

    private List<(string FilePath, DateTime LastModified, long FileSize)> BuildMetaEntries(List<string> paths)
    {
        var entries = new List<(string FilePath, DateTime LastModified, long FileSize)>(paths.Count);
        foreach (var path in paths)
        {
            try
            {
                var fi = new FileInfo(path);
                entries.Add((path, fi.LastWriteTimeUtc, fi.Length));
            }
            catch
            {
                entries.Add((path, DateTime.MinValue, 0));
            }
        }
        return entries;
    }

    private Dictionary<string, (DateTime LastModified, long FileSize)> BuildMetaLookup(List<string> paths)
    {
        var lookup = new Dictionary<string, (DateTime LastModified, long FileSize)>(paths.Count);
        foreach (var path in paths)
        {
            try
            {
                var fi = new FileInfo(path);
                lookup[path] = (fi.LastWriteTimeUtc, fi.Length);
            }
            catch
            {
                lookup[path] = (DateTime.MinValue, 0);
            }
        }
        return lookup;
    }

    private async Task<Dictionary<string, float[]?>> BatchGetCacheAsync(
        List<(string FilePath, DateTime LastModified, long FileSize)> entries)
    {
        try
        {
            return await _store.GetBatchAsync(ModelName, entries).ConfigureAwait(false);
        }
        catch
        {
            return new Dictionary<string, float[]?>();
        }
    }

    internal static List<string> ScanArchivesRecursive(string directoryPath)
    {
        var files = new List<string>();
        foreach (var pattern in ArchivePatterns)
        {
            files.AddRange(Directory.GetFiles(directoryPath, pattern, SearchOption.AllDirectories));
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
        int OriginalIndex,
        string ArchivePath,
        int PageIndex,
        string CacheKey,
        DateTime LastModified,
        long FileSize,
        byte[] ImageData);
}