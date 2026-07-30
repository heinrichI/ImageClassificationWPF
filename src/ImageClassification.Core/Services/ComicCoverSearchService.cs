using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace ImageClassification.Core.Services;

/// <summary>
/// Scans CBZ/CBR archives, extracts first-image covers, and ranks them
/// by CLIP cosine similarity against a user text query.
/// Uses IClipImageEncoder (shared, cached) for image embeddings
/// and IClipTextEncoder for proper CLIP text embeddings.
/// </summary>
internal sealed class ComicCoverSearchService : IComicCoverSearchService
{
    private const int EmbeddingDim = 512;
    private static readonly string[] ArchivePatterns = { "*.cbz", "*.cbr", "*.cb7", "*.cbt" };

    private readonly IArchiveReader _archiveReader;
    private readonly IClipImageEncoder _clipEncoder;
    private readonly IClipTextEncoder _textEncoder;
    private readonly IVectorStore _store;
    private readonly ILogger<ComicCoverSearchService> _logger;

    public ComicCoverSearchService(
        IArchiveReader archiveReader,
        IClipImageEncoder clipEncoder,
        IClipTextEncoder textEncoder,
        IVectorStore store,
        ILogger<ComicCoverSearchService> logger)
    {
        _archiveReader = archiveReader ?? throw new ArgumentNullException(nameof(archiveReader));
        _clipEncoder = clipEncoder ?? throw new ArgumentNullException(nameof(clipEncoder));
        _textEncoder = textEncoder ?? throw new ArgumentNullException(nameof(textEncoder));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task LoadModelAsync(string clipOnnxPath)
    {
        // No-op — model loading is delegated to IClipImageEncoder
        return Task.CompletedTask;
    }

    public async Task<List<ComicCoverResult>> SearchAsync(
        string directoryPath,
        string query,
        IProgress<(int Current, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(directoryPath))
            return new List<ComicCoverResult>();

        // 1. Recursively find all archives
        var archiveFiles = ScanArchivesRecursive(directoryPath);
        if (archiveFiles.Count == 0)
            return new List<ComicCoverResult>();

        // 2. Encode the text query once using real CLIP text encoder
        // Use a prompt template that matches CLIP's training distribution
        var textQuery = $"a photo of {query}";
        var queryEmbedding = await _textEncoder.EncodeTextAsync(textQuery, ct).ConfigureAwait(false);

        var bag = new ConcurrentBag<(int Index, ComicCoverResult Result)>();

        await Task.Run(() =>
        {
            int completed = 0;

            Parallel.ForEach(archiveFiles,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                (archivePath, state, index) =>
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var fi = new FileInfo(archivePath);
                        var lastModified = fi.LastWriteTimeUtc;
                        var fileSize = fi.Length;

                        // Check archive-level cache first
                        var cachedEmbedding = _store.GetAsync("ClipViTB32",
                                archivePath, lastModified, fileSize)
                            .GetAwaiter().GetResult();

                        string? coverPath = null;
                        float[] imageEmbedding;

                        if (cachedEmbedding is not null)
                        {
                            imageEmbedding = cachedEmbedding;
                        }
                        else
                        {
                            _logger.LogDebug("Archive cache miss: {ArchivePath}", archivePath);

                            coverPath = _archiveReader.ExtractFirstImageAsync(archivePath)
                                .GetAwaiter().GetResult();

                            if (coverPath is null)
                                return;

                            imageEmbedding = _clipEncoder.EncodeImageAsync(coverPath, ct)
                                .GetAwaiter().GetResult();

                            _store.SaveAsync("ClipViTB32",
                                    archivePath, lastModified, fileSize, imageEmbedding)
                                .GetAwaiter().GetResult();
                        }

                        // On cache hit we still need the cover file for display
                        coverPath ??= _archiveReader.ExtractFirstImageAsync(archivePath)
                            .GetAwaiter().GetResult();

                        if (coverPath is null)
                            return;

                        var score = CosineSimilarity(imageEmbedding, queryEmbedding);

                        bag.Add(((int)index, new ComicCoverResult
                        {
                            ArchivePath = archivePath,
                            ArchiveFileName = Path.GetFileName(archivePath),
                            CoverImagePath = coverPath,
                            SimilarityScore = score
                        }));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, ex.Message);
                    }

                    var count = Interlocked.Increment(ref completed);
                    progress?.Report((count, archiveFiles.Count));
                });
        }, ct);

        return bag
            .OrderByDescending(x => x.Result.SimilarityScore)
            .Select(x => x.Result)
            .ToList();
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
        // IClipImageEncoder and IClipTextEncoder are shared — don't dispose them here
        GC.SuppressFinalize(this);
    }
}