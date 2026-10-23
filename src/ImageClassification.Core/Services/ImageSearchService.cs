using System.Diagnostics;
using ImageClassification.Core.Helpers;
using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using Microsoft.Extensions.Logging;

namespace ImageClassification.Core.Services;

/// <summary>
/// CLIP text→image search over plain image files, batched exactly like the comic cover
/// search:
/// <list type="number">
/// <item>the per-file vector cache is probed with a single sequential scan of the
/// model's rows (the fastest strategy per benchmarks/ProbeBenchmark; no SQL
/// host-parameter limits — see <see cref="IVectorStore.GetBatchAsync"/>);</item>
/// <item>cache hits are scored immediately without reading the file;</item>
/// <item>misses are encoded by <see cref="BatchImageEncoder"/> — batched ONNX inference
/// (GPU when available, CPU fallback) whose internal producer/consumer keeps CPU
/// preprocessing ahead of GPU runs. The batch size is the live user setting
/// (Settings → GpuBatchSize, default 32);</item>
/// <item>new embeddings are saved back to the per-file cache, so a second search of the
/// same directory is a near-instant cache probe.</item>
/// </list>
/// The query is encoded with the same "a photo of ..." prefix as the comic cover search,
/// so threshold values stay comparable across the two search tabs.
/// Encoding is chunked by the encoder's live batch size; after each chunk the vectors
/// are scored and saved to the per-file cache, so a cancelled run keeps its partial
/// results AND a partially filled cache (the next search resumes instead of starting over).
/// Files that fail to preprocess come back as zero vectors from the encoder and are
/// excluded from the results with a note (they score 0 against anything).
/// </summary>
internal sealed class ImageSearchService : IImageSearchService
{
    private const string ModelName = "ClipViTB32";

    /// <summary>Image extensions the search scans (case-insensitive via <see cref="MatchType.Simple"/>).</summary>
    private static readonly string[] ImagePatterns =
    {
        "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.tiff", "*.webp"
    };

    private readonly IClipTextEncoder _textEncoder;
    private readonly IBatchImageEncoder _batchEncoder;
    private readonly IVectorStore _store;
    private readonly ILogger<ImageSearchService> _logger;

    public ImageSearchService(
        IClipTextEncoder textEncoder,
        IBatchImageEncoder batchEncoder,
        IVectorStore store,
        ILogger<ImageSearchService> logger)
    {
        _textEncoder = textEncoder ?? throw new ArgumentNullException(nameof(textEncoder));
        _batchEncoder = batchEncoder ?? throw new ArgumentNullException(nameof(batchEncoder));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<List<ImageSearchResult>> SearchAsync(
        string directoryPath,
        string query,
        ImageSearchOptions? options = null,
        IProgress<ImageSearchStatus>? status = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(directoryPath))
        {
            _logger.LogWarning("Image search: directory not found: {Dir}", directoryPath);
            status?.Report(new ImageSearchStatus
            {
                Note = $"Directory not found, skipped: {directoryPath}",
                NotePath = directoryPath
            });
            return [];
        }

        // 1. Scan image files (top level or the whole tree, per the caller's option),
        //    capturing file metadata for the cache probe.
        var recurse = options?.RecurseSubfolders ?? true;
        var scanOptions = new EnumerationOptions
        {
            RecurseSubdirectories = recurse,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple
        };

        var files = new List<ImageWorkItem>();
        // Throttled scan ticks: a 300k-file tree on an external drive can take minutes
        // to enumerate — without progress the UI looks frozen on the previous phase.
        var scanReporter = new ThrottledStatusReporter<ImageSearchStatus>(status, ct);
        var scanSw = Stopwatch.StartNew();
        foreach (var pattern in ImagePatterns)
        {
            foreach (var path in Directory.EnumerateFiles(directoryPath, pattern, scanOptions))
            {
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
                    fileSize = 0; // stat raced with a deletion — treat as a cache miss
                }

                files.Add(new ImageWorkItem(path, lastModified, fileSize));

                if ((files.Count & 0x3FF) == 0)
                    scanReporter.Report(new ImageSearchStatus
                    {
                        Phase = $"Scanning images: {directoryPath}",
                        Detail = $"{files.Count:N0} files so far",
                        Current = files.Count,
                    });
            }
        }
        _logger.LogInformation(
            "Image search: scanned {Count} file(s) under {Dir} in {Ms} ms",
            files.Count, directoryPath, (long)scanSw.ElapsedMilliseconds);

        if (files.Count == 0)
        {
            _logger.LogInformation("Image search: no images found in {Dir}", directoryPath);
            status?.Report(new ImageSearchStatus { Phase = "No images found in the selected directory" });
            return [];
        }

        status?.Report(new ImageSearchStatus { Phase = $"Found {files.Count} images", Current = 0, Total = files.Count });

        // 2. Query embedding — same "a photo of" prefix as the comic cover search so the
        //    two tabs share one query/image embedding space.
        status?.Report(new ImageSearchStatus { Phase = "Encoding query text..." });
        var encSw = Stopwatch.StartNew();
        var queryEmbedding = await _textEncoder.EncodeTextAsync($"a photo of {query}", ct)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Image search: query text encoded in {Ms} ms", (long)encSw.ElapsedMilliseconds);

        // 3. The store's one-time legacy migration runs on a background thread at startup.
        //    It has almost always finished by the time a search starts; if not, wait for
        //    it here and say so explicitly instead of stalling at 0/N.
        var migWait = DateTime.UtcNow;
        await _store.WhenReady.ConfigureAwait(false);
        var migMs = (DateTime.UtcNow - migWait).TotalMilliseconds;
        if (migMs > 1000)
        {
            status?.Report(new ImageSearchStatus
                { Phase = "Vector cache: legacy migration finished — resuming..." });
        }

        // 4. Cache probe — a single sequential scan of the model's cached rows
        //    (ProbeBenchmark: 3–13x faster than parameterized IN/temp-table variants).
        //    Hits are scored immediately (the file is never read); misses go to the
        //    batch encoder.
        status?.Report(new ImageSearchStatus { Phase = "Probing embedding cache..." });
        ct.ThrowIfCancellationRequested();

        var probeSw = Stopwatch.StartNew();
        var hits = new List<ImageWorkItem>(files.Count);
        var misses = new List<ImageWorkItem>(files.Count);
        await SplitByCacheProbeAsync(files, hits, misses).ConfigureAwait(false);

        var cacheHits = hits.Count;
        _logger.LogInformation(
            "Image search: cache probe took {Ms} ms — {Hits} of {Total} files served from the cache ({Miss} to encode)",
            (long)probeSw.ElapsedMilliseconds, cacheHits, files.Count, misses.Count);
        var results = new List<ImageSearchResult>(files.Count);
        for (var i = 0; i < cacheHits; i++)
        {
            var item = hits[i];
            // SplitByCacheProbe stored the cached embedding in the work item for hits.
            results.Add(ToResult(item, VectorMath.CosineSimilarity(item.CachedEmbedding!, queryEmbedding)));
        }

        // Hits are scored immediately; the comment documents why the work item carries
        // the embedding (cache hits are never read from disk).
        status?.Report(new ImageSearchStatus
        {
            Phase = cacheHits == files.Count
                ? $"All {files.Count} images cached — nothing to encode"
                : $"Cache probe: {cacheHits} cached, {misses.Count} to encode",
            Current = cacheHits,
            Total = files.Count
        });

        // 5. Encode the misses chunk-by-chunk in GPU batches, exactly like the comic
        //    pipeline: after EVERY chunk the vectors are scored AND saved to the
        //    per-file cache. A cancelled run therefore keeps its results up to the last
        //    finished batch and persists everything encoded so far — the next search
        //    resumes from the cache instead of starting over from zero.
        if (misses.Count > 0)
        {
            var detailStatus = new ThrottledStatusReporter<ImageSearchStatus>(status, ct);
            var batchSize = Math.Max(1, _batchEncoder.BatchSize);
            var skipped = 0;
            var saved = 0;
            var encoded = 0;
            var batchesDone = 0;

            for (var start = 0; start < misses.Count; start += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = misses.GetRange(start, Math.Min(batchSize, misses.Count - start));

                var chunkPaths = new List<string>(chunk.Count);
                foreach (var item in chunk)
                    chunkPaths.Add(item.Path);

                var chunkVectors = await _batchEncoder
                    .EncodeImagesBatchAsync(chunkPaths, null, ct)
                    .ConfigureAwait(false);

                batchesDone++;
                encoded += chunk.Count;

                // 6. Score the chunk's vectors and persist them to the per-file cache.
                //    Zero vectors = the file could not be read/decoded → note, no result.
                for (var i = 0; i < chunk.Count; i++)
                {
                    var item = chunk[i];
                    var embedding = chunkVectors[i];
                    if (IsZeroVector(embedding))
                    {
                        skipped++;
                        _logger.LogWarning("Image search: could not read or decode {Path}", item.Path);
                        status?.Report(new ImageSearchStatus
                        {
                            Note = $"Skipped {item.Path}: could not be read or decoded",
                            NotePath = item.Path
                        });
                        continue;
                    }

                    results.Add(ToResult(item, VectorMath.CosineSimilarity(embedding, queryEmbedding)));
                    if (await TrySaveCachedAsync(item, embedding))
                        saved++;
                }

                var current = cacheHits + encoded;
                status?.Report(new ImageSearchStatus
                {
                    Current = current,
                    Total = files.Count,
                    CurrentFile = chunk[^1].Path
                });
                detailStatus.Report(new ImageSearchStatus
                {
                    Detail = $"Processing images: {current}/{files.Count} · GPU {batchesDone} batches",
                    Current = current,
                    Total = files.Count
                });
            }

            _logger.LogInformation(
                "Image search: encoded {Total} missing file(s) in {Batches} GPU batch(es), {Saved} cached, {Skipped} unreadable",
                misses.Count, (misses.Count + batchSize - 1) / batchSize, saved, skipped);
        }

        // 7. Rank: best match first. The VM filters by its similarity threshold.
        status?.Report(new ImageSearchStatus
        {
            Phase = "Scoring and ranking results...",
            Current = files.Count,
            Total = files.Count
        });

        return results.OrderByDescending(r => r.SimilarityScore).ToList();
    }

    /// <summary>
    /// Batched cache probe. A throwing store (locked/corrupt DB) must not break the
    /// search: everything is treated as a miss and re-encoded instead of masquerading
    /// as "cache not working" (same convention as the comic pipeline's probe helpers).
    /// </summary>
    private async Task SplitByCacheProbeAsync(
        List<ImageWorkItem> files, List<ImageWorkItem> hits, List<ImageWorkItem> misses)
    {
        try
        {
            var entries = files.Select(f => (f.Path, f.LastModified, f.FileSize)).ToList();
            var cached = await _store.GetBatchAsync(ModelName, entries).ConfigureAwait(false);

            for (var i = 0; i < files.Count; i++)
            {
                var embedding = cached.TryGetValue(files[i].Path, out var vec) ? vec : null;
                if (embedding is not null)
                    hits.Add(new ImageWorkItem(files[i].Path, files[i].LastModified, files[i].FileSize, embedding));
                else
                    misses.Add(files[i]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image search: vector store probe failed — all files treated as misses");
            misses.AddRange(files);
        }
    }

    /// <summary>Best-effort per-file cache save — a failure only means re-encoding next run.</summary>
    private async Task<bool> TrySaveCachedAsync(ImageWorkItem item, float[] embedding)
    {
        try
        {
            await _store.SaveAsync(ModelName, item.Path, item.LastModified, item.FileSize, embedding)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image search: cache save failed for {Path} — it will be re-encoded on the next search",
                item.Path);
            return false;
        }
    }

    private static ImageSearchResult ToResult(ImageWorkItem item, float score) => new()
    {
        FilePath = item.Path,
        FileName = Path.GetFileName(item.Path),
        SimilarityScore = score
    };

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
    /// One file of the search: path + the metadata the cache rows are keyed by, plus the
    /// cached embedding (non-null only for cache hits, which are never encoded).
    /// </summary>
    private readonly record struct ImageWorkItem(string Path, DateTime LastModified, long FileSize, float[]? CachedEmbedding = null);
}