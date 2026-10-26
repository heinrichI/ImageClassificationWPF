using System.Diagnostics;
using ImageClassification.Core.Helpers;
using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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
    private readonly IClipImageEncoder _imageEncoder;
    private readonly IBatchImageEncoder _batchEncoder;
    private readonly IVectorStore _store;
    private readonly ILogger<ImageSearchService> _logger;

    public ImageSearchService(
        IClipTextEncoder textEncoder,
        IClipImageEncoder imageEncoder,
        IBatchImageEncoder batchEncoder,
        IVectorStore store,
        ILogger<ImageSearchService> logger)
    {
        _textEncoder = textEncoder ?? throw new ArgumentNullException(nameof(textEncoder));
        _imageEncoder = imageEncoder ?? throw new ArgumentNullException(nameof(imageEncoder));
        _batchEncoder = batchEncoder ?? throw new ArgumentNullException(nameof(batchEncoder));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<List<ImageSearchResult>> SearchAsync(
        string directoryPath,
        string query,
        ImageSearchOptions? options = null,
        IProgress<ImageSearchStatus>? status = null,
        CancellationToken ct = default)
    {
        // Text query — same "a photo of" prefix as the comic cover search so the two
        // tabs share one query/image embedding space.
        return SearchCoreAsync(directoryPath, options, status, ct,
            async ct2 =>
            {
                status?.Report(new ImageSearchStatus { Phase = "Encoding query text..." });
                var encSw = Stopwatch.StartNew();
                var embedding = await _textEncoder.EncodeTextAsync($"a photo of {query}", ct2)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "Image search: query text encoded in {Ms} ms", (long)encSw.ElapsedMilliseconds);
                return embedding;
            },
            excludeImagePath: null,
            queryDescription: $"text '{query}'");
    }

    /// <inheritdoc />
    public Task<List<ImageSearchResult>> SearchByImageAsync(
        string directoryPath,
        string queryImagePath,
        ImageSearchOptions? options = null,
        IProgress<ImageSearchStatus>? status = null,
        CancellationToken ct = default)
    {
        // Fail fast BEFORE the (potentially long) directory scan: an unreadable/undecodable
        // query image makes the whole search pointless. The encoder re-checks at encode time.
        if (!File.Exists(queryImagePath))
        {
            _logger.LogWarning("Image search: query image not found: {Path}", queryImagePath);
            status?.Report(new ImageSearchStatus
            {
                Note = $"Query image not found: {queryImagePath}",
                NotePath = queryImagePath
            });
            return Task.FromResult(new List<ImageSearchResult>());
        }

        try
        {
            using var probe = Image.Load<Rgb24>(queryImagePath); // decode-only pre-check
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image search: query image could not be decoded: {Path}", queryImagePath);
            status?.Report(new ImageSearchStatus
            {
                Note = $"Query image could not be read/decoded: {queryImagePath}",
                NotePath = queryImagePath
            });
            return Task.FromResult(new List<ImageSearchResult>());
        }

        return SearchCoreAsync(directoryPath, options, status, ct,
            async ct2 =>
            {
                status?.Report(new ImageSearchStatus { Phase = "Encoding query image..." });
                var encSw = Stopwatch.StartNew();
                // The registered encoder is the caching decorator: for a file that was
                // ever searched this is a plain vector-store lookup — "copy the ViT hash".
                var embedding = await _imageEncoder.EncodeImageAsync(queryImagePath, ct2)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "Image search: query image encoded in {Ms} ms", (long)encSw.ElapsedMilliseconds);
                return embedding;
            },
            excludeImagePath: queryImagePath,
            queryDescription: queryImagePath);
    }

    /// <inheritdoc />
    public Task<List<ImageSearchResult>> SearchByVectorAsync(
        string directoryPath,
        float[] queryVector,
        ImageSearchOptions? options = null,
        IProgress<ImageSearchStatus>? status = null,
        CancellationToken ct = default)
    {
        // The vector is user-provided (pasted hash) — an empty one is a UI input
        // mistake, report it before the (potentially long) directory scan.
        if (queryVector is null || queryVector.Length == 0)
        {
            status?.Report(new ImageSearchStatus
            {
                Note = "Query hash is empty — paste the base64 hash copied from a result tile (right-click → Copy hash)."
            });
            return Task.FromResult(new List<ImageSearchResult>());
        }

        return SearchCoreAsync(directoryPath, options, status, ct,
            _ =>
            {
                // No encoding at all: the pasted vector IS the query ("search by hash").
                status?.Report(new ImageSearchStatus
                    { Phase = $"Using query hash ({queryVector.Length} floats)..." });
                return Task.FromResult(queryVector);
            },
            excludeImagePath: null,
            queryDescription: $"hash ({queryVector.Length} floats)");
    }

    /// <inheritdoc />
    public async Task<string?> GetImageHashAsync(string imagePath, CancellationToken ct = default)
    {
        if (!File.Exists(imagePath))
            return null;

        DateTime lastModified;
        long fileSize;
        try
        {
            var fi = new FileInfo(imagePath);
            lastModified = fi.LastWriteTimeUtc;
            fileSize = fi.Length;
        }
        catch
        {
            lastModified = DateTime.MinValue; // stat raced a deletion — treat as a cache miss
            fileSize = 0;
        }

        // The store's one-time legacy migration must be done before the lookup can
        // see migrated rows (same wait the search performs).
        await _store.WhenReady.ConfigureAwait(false);

        // Cache first: one SQL lookup for a file that was ever searched.
        try
        {
            var cached = await _store.GetAsync(ModelName, imagePath, lastModified, fileSize).ConfigureAwait(false);
            if (cached is not null && !IsZeroVector(cached))
                return VectorCodec.ToBase64(cached);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image hash: cache lookup failed for {Path} — falling back to encoding", imagePath);
        }

        // Cache miss: encode with the shared caching encoder (it saves the result
        // back, so the next copy of the same hash is a plain lookup again).
        var embedding = await _imageEncoder.EncodeImageAsync(imagePath, ct).ConfigureAwait(false);
        if (embedding is null || IsZeroVector(embedding))
            return null; // undecodable file

        return VectorCodec.ToBase64(embedding);
    }

    /// <summary>
    /// Shared pipeline for both query kinds. <paramref name="queryFactory"/> produces the
    /// query embedding at the same point the text query used to be encoded (after the
    /// file scan), so the phase order on screen is identical for both modes. A failing
    /// query (e.g. the model cannot process the query image) aborts the search with a
    /// note; cancellation propagates.
    /// </summary>
    private async Task<List<ImageSearchResult>> SearchCoreAsync(
        string directoryPath,
        ImageSearchOptions? options,
        IProgress<ImageSearchStatus>? status,
        CancellationToken ct,
        Func<CancellationToken, Task<float[]>> queryFactory,
        string? excludeImagePath,
        string queryDescription)
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

        // 2. Query embedding — produced by the caller's factory (text or image variant).
        //    A failing query aborts the whole search; cancellation propagates.
        ct.ThrowIfCancellationRequested();
        float[] queryEmbedding;
        try
        {
            queryEmbedding = await queryFactory(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image search: query encoding failed: {Description}", queryDescription);
            status?.Report(new ImageSearchStatus
            {
                Note = $"Query could not be encoded: {queryDescription}"
            });
            return [];
        }
        Debug.Assert(queryEmbedding is { Length: > 0 }, "query embedding must not be empty");

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

        // A pasted hash can come from a DIFFERENT model (different dimension). Scoring
        // such a vector would trip the dimension assert (Debug) or silently produce
        // garbage (Release) — validate against the first cached vector BEFORE any
        // scoring and bail out with a note. Cached rows of one model always share a
        // dimension, so the first hit is a valid sample.
        if (cacheHits > 0 && hits[0].CachedEmbedding!.Length != queryEmbedding.Length)
        {
            status?.Report(new ImageSearchStatus
            {
                Note = $"Query hash has {queryEmbedding.Length} floats but the cached embeddings have {hits[0].CachedEmbedding!.Length} — it was copied from a different model."
            });
            return [];
        }

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

                // Dimension guard for the all-misses case (no cached sample above):
                // the first encoded chunk of the first batch carries the model's
                // dimension — a mismatched pasted hash is rejected before any scoring.
                if (batchesDone == 1 && chunkVectors.Length > 0)
                {
                    for (var i = 0; i < chunkVectors.Length; i++)
                    {
                        if (IsZeroVector(chunkVectors[i]))
                            continue;
                        if (chunkVectors[i].Length != queryEmbedding.Length)
                        {
                            status?.Report(new ImageSearchStatus
                            {
                                Note = $"Query hash has {queryEmbedding.Length} floats but the model produces {chunkVectors[i].Length} — it was copied from a different model."
                            });
                            return [];
                        }
                        break; // one non-zero sample is enough
                    }
                }

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

        // 7. Rank: best match first. The query file itself (image mode) is excluded —
        //    it would always score ~100% and sit on top of the list. The VM filters by
        //    its similarity threshold.
        status?.Report(new ImageSearchStatus
        {
            Phase = "Scoring and ranking results...",
            Current = files.Count,
            Total = files.Count
        });

        return results
            .Where(r => excludeImagePath is null || !IsSamePath(r.FilePath, excludeImagePath))
            .OrderByDescending(r => r.SimilarityScore)
            .ToList();
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

    private static ImageSearchResult ToResult(ImageWorkItem item, float score)
    {
        // A NaN score would silently sink the file to the bottom of the ranked list.
        Debug.Assert(!float.IsNaN(score), "similarity score must not be NaN");
        return new()
        {
            FilePath = item.Path,
            FileName = Path.GetFileName(item.Path),
            SimilarityScore = score
        };
    }

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
    /// Path equality for the query-file exclusion: full-path normalized and
    /// case-insensitive (directory enumeration can return a different case format
    /// than the caller's path). GetFullPath failures (weird UNC names) fall back to
    /// a direct comparison rather than breaking the search.
    /// </summary>
    private static bool IsSamePath(string a, string? b)
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
    /// One file of the search: path + the metadata the cache rows are keyed by, plus the
    /// cached embedding (non-null only for cache hits, which are never encoded).
    /// </summary>
    private readonly record struct ImageWorkItem(string Path, DateTime LastModified, long FileSize, float[]? CachedEmbedding = null);
}