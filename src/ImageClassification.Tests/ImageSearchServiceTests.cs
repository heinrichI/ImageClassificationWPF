using System.Collections.Concurrent;
using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ImageClassification.Tests;

/// <summary>
/// Tests for <see cref="ImageSearchService"/> (CLIP text→image search over plain image
/// files, batched like the comic cover search). The CLIP text encoder, the batch image
/// encoder and the vector store are mocked with deterministic 4-d vectors, so the
/// expected cosine similarities are exact:
///   query  = [1, 0, 0, 0]
///   a.jpg  = [1, 0, 0, 0]        → 1.0
///   b.png  = [0.6, 0.8, 0, 0]    → 0.6   (L2-normalized)
///   c.tiff = [0, 1, 0, 0]        → 0.0
/// </summary>
public class ImageSearchServiceTests : IDisposable
{
    private static readonly float[] QueryVector = [1f, 0f, 0f, 0f];

    private readonly string _tempDir;
    private readonly Mock<IClipTextEncoder> _textEncoder = new();
    private readonly Mock<IBatchImageEncoder> _batchEncoder = new();
    private readonly Mock<IVectorStore> _store = new();
    private readonly ImageSearchService _service;

    /// <summary>File names the batch encoder was asked to encode (across all calls).</summary>
    private readonly List<string> _encodedPaths = new();

    public ImageSearchServiceTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("image-search-svc-").FullName;

        // Query encoding: deterministic, independent of the text.
        _textEncoder
            .Setup(e => e.EncodeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, _) => Task.FromResult(QueryVector));

        // Vector store: everything is a cache miss by default (tests that need hits
        // re-configure GetBatchAsync), saving is a no-op, migration is already done.
        _store.Setup(s => s.WhenReady).Returns(Task.CompletedTask);
        _store.Setup(s => s.GetBatchAsync(It.IsAny<string>(), It.IsAny<List<(string FilePath, DateTime LastModified, long FileSize)>>()))
            .Returns<string, List<(string FilePath, DateTime LastModified, long FileSize)>>((_, entries) =>
                Task.FromResult(GetBatchFactory?.Invoke(entries) ?? new Dictionary<string, float[]?>()));
        _store.Setup(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<long>(), It.IsAny<float[]>()))
            .Callback<string, string, DateTime, long, float[]>((_, path, _, _, _) => Interlocked.Increment(ref _savedCount))
            .Returns(Task.CompletedTask);

        // Batch encoder: deterministic per-file vectors by file name, progress is invoked
        // after the "whole list" is done (like the real encoder's per-batch reports).
        _batchEncoder
            .Setup(e => e.EncodeImagesBatchAsync(It.IsAny<List<string>>(), It.IsAny<IProgress<(int Current, int Total)>>(), It.IsAny<CancellationToken>()))
            .Returns<List<string>, IProgress<(int Current, int Total)>, CancellationToken>((paths, progress, _) =>
            {
                lock (_encodedPaths)
                {
                    _encodedPaths.AddRange(paths);
                }
                progress?.Report((paths.Count, paths.Count));
                return Task.FromResult(paths.Select(p => ImageVectorFor(p)).ToArray());
            });
        _batchEncoder
            .SetupGet(e => e.BatchSize)
            .Returns(32);

        _service = new ImageSearchService(
            _textEncoder.Object, _batchEncoder.Object, _store.Object, NullLogger<ImageSearchService>.Instance);

        CreateFile(Path.Combine(_tempDir, "a.jpg"));
        CreateFile(Path.Combine(_tempDir, "b.png"));
        CreateFile(Path.Combine(_tempDir, "notes.txt"));   // not an image — must be ignored by the scan
        CreateFile(Path.Combine(_tempDir, "d.gif"));       // unsupported extension — must be ignored by the scan
        var sub = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(sub);
        CreateFile(Path.Combine(sub, "c.tiff"));
    }

    public void Dispose()
    {
        _textEncoder.Object.Dispose();
        _batchEncoder.Object.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Per-test cache probe override: given the probed entries, returns the dictionary of
    /// fresh cached embeddings (absent key = miss). Null (default) = every file is a miss.
    /// </summary>
    public Func<List<(string FilePath, DateTime LastModified, long FileSize)>, Dictionary<string, float[]?>>? GetBatchFactory { get; set; }

    private int _savedCount;

    private void CreateFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, new byte[] { 0x01, 0x02 });
    }

    private static float[] ImageVectorFor(string path)
    {
        return Path.GetFileName(path) switch
        {
            "a.jpg" => [1f, 0f, 0f, 0f],
            "b.png" => [0.6f, 0.8f, 0f, 0f],
            "c.tiff" => [0f, 1f, 0f, 0f],
            _ => throw new InvalidOperationException($"No vector registered for {path}")
        };
    }

    private static float[] ZeroVector() => new float[4];

    private List<string> EncodedNames()
    {
        lock (_encodedPaths)
            return _encodedPaths.Select(Path.GetFileName).ToList();
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public readonly List<T> Items = new();
        public void Report(T value)
        {
            lock (Items) Items.Add(value);
        }
    }

    [Fact]
    public async Task SearchAsync_AllMisses_RanksBySimilarity_AndIncludesSubfolders_WhenRecurse()
    {
        var results = await _service.SearchAsync(_tempDir, "autumn",
            new ImageSearchOptions { RecurseSubfolders = true });

        Assert.Equal(3, results.Count);                    // a.jpg, b.png, c.tiff
        Assert.Equal("a.jpg", results[0].FileName);
        Assert.Equal("b.png", results[1].FileName);
        Assert.Equal("c.tiff", results[2].FileName);
        Assert.Equal(1.0f, results[0].SimilarityScore, 3);
        Assert.Equal(0.6f, results[1].SimilarityScore, 3);

        // All three scanned files were misses → all three went to the batch encoder.
        Assert.Equal(3, EncodedNames().Count);
    }

    [Fact]
    public async Task SearchAsync_TopLevelOnly_WhenRecurseIsFalse()
    {
        var results = await _service.SearchAsync(_tempDir, "autumn",
            new ImageSearchOptions { RecurseSubfolders = false });

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, r => r.FileName == "c.tiff");
        Assert.Equal(2, EncodedNames().Count);
    }

    [Fact]
    public async Task SearchAsync_UsesDefaultRecurse_WhenOptionsIsNull()
    {
        var results = await _service.SearchAsync(_tempDir, "autumn");

        Assert.Equal(3, results.Count); // default RecurseSubfolders = true
    }

    [Fact]
    public async Task SearchAsync_MissingDirectory_ReturnsEmpty_WithNote()
    {
        var status = new RecordingProgress<ImageSearchStatus>();
        var results = await _service.SearchAsync(Path.Combine(_tempDir, "no-such-dir"), "q",
            status: status);

        Assert.Empty(results);
        Assert.Contains(status.Items, s => s.Note is not null && s.Note.Contains("no-such-dir"));
    }

    [Fact]
    public async Task SearchAsync_EmptyDirectory_ReportsNoImagesPhase()
    {
        var empty = Directory.CreateTempSubdirectory("image-search-empty-").FullName;
        try
        {
            var status = new RecordingProgress<ImageSearchStatus>();
            var results = await _service.SearchAsync(empty, "q", status: status);

            Assert.Empty(results);
            Assert.Contains(status.Items, s => s.Phase.Contains("No images found"));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public async Task SearchAsync_CacheHits_ScoredWithoutCallingEncoder()
    {
        GetBatchFactory = entries => entries
            .Where(e => e.FilePath.EndsWith(".jpg") || e.FilePath.EndsWith(".png") || e.FilePath.EndsWith(".tiff"))
            .ToDictionary(e => e.FilePath, e => ImageVectorFor(e.FilePath));

        var results = await _service.SearchAsync(_tempDir, "autumn");

        Assert.Equal(3, results.Count);
        Assert.Equal("a.jpg", results[0].FileName); // ranking still correct from cached vectors
        Assert.Empty(EncodedNames());               // nothing was encoded
        Assert.Equal(0, _savedCount);               // nothing new to save
    }

    [Fact]
    public async Task SearchAsync_MixedHitsAndMisses_EncodesOnlyMisses_AndSavesThem()
    {
        // a.jpg is cached; b.png and c.tiff are misses.
        GetBatchFactory = entries => entries
            .Where(e => e.FilePath.EndsWith(".jpg"))
            .ToDictionary(e => e.FilePath, e => ImageVectorFor(e.FilePath));

        var results = await _service.SearchAsync(_tempDir, "autumn");

        Assert.Equal(3, results.Count);
        Assert.Equal("a.jpg", results[0].FileName);

        // Exactly the two misses went through the encoder (in list order).
        var encoded = EncodedNames();
        Assert.Equal(2, encoded.Count);
        Assert.DoesNotContain("a.jpg", encoded);
        Assert.Contains("b.png", encoded);
        Assert.Contains("c.tiff", encoded);

        // Both miss embeddings were saved to the per-file cache under the CLIP model.
        _store.Verify(s => s.SaveAsync("ClipViTB32", It.IsAny<string>(), It.IsAny<DateTime>(),
            It.IsAny<long>(), It.IsAny<float[]>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SearchAsync_ZeroVector_IsSkipped_WithNote_AndNotSaved()
    {
        // The batch encoder returns a zero vector for b.png (unreadable/corrupt file).
        _batchEncoder
            .Setup(e => e.EncodeImagesBatchAsync(It.IsAny<List<string>>(), It.IsAny<IProgress<(int Current, int Total)>>(), It.IsAny<CancellationToken>()))
            .Returns<List<string>, IProgress<(int Current, int Total)>, CancellationToken>((paths, progress, _) =>
            {
                lock (_encodedPaths) { _encodedPaths.AddRange(paths); }
                progress?.Report((paths.Count, paths.Count));
                var vectors = paths.Select(p =>
                    Path.GetFileName(p) == "b.png" ? ZeroVector() : ImageVectorFor(p)).ToArray();
                return Task.FromResult(vectors);
            });

        var status = new RecordingProgress<ImageSearchStatus>();
        var results = await _service.SearchAsync(_tempDir, "autumn", status: status);

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, r => r.FileName == "b.png");
        Assert.Contains(status.Items, s => s.Note is not null && s.Note.Contains("b.png"));
        Assert.Equal(2, _savedCount); // only a.jpg and c.tiff were cached
    }

    [Fact]
    public async Task SearchAsync_ProbeFailure_AllFilesReencoded()
    {
        _store.Setup(s => s.GetBatchAsync(It.IsAny<string>(), It.IsAny<List<(string FilePath, DateTime LastModified, long FileSize)>>()))
            .ThrowsAsync(new IOException("vector cache locked"));

        var results = await _service.SearchAsync(_tempDir, "autumn");

        Assert.Equal(3, results.Count);
        Assert.Equal(3, EncodedNames().Count); // the probe failure degrades to a full re-encode
    }

    [Fact]
    public async Task SearchAsync_ReportsPhases_AndProgressTicks()
    {
        var status = new RecordingProgress<ImageSearchStatus>();
        await _service.SearchAsync(_tempDir, "autumn", status: status);

        Assert.Contains(status.Items, s => s.Phase == "Found 3 images" && s.Total == 3);
        Assert.Contains(status.Items, s => s.Phase is not null && s.Phase.Contains("Cache probe"));
        Assert.Contains(status.Items, s => s.Phase == "Scoring and ranking results..."
            && s.Current == 3 && s.Total == 3);
    }

    [Fact]
    public async Task SearchAsync_EncodesQueryWithPhotoPrefix()
    {
        await _service.SearchAsync(_tempDir, "autumn");

        _textEncoder.Verify(e => e.EncodeTextAsync("a photo of autumn", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SearchAsync_CancelledMidEncode_KeepsPartialResults_AndSavedCache()
    {
        // BatchSize 2 → chunks [a.jpg, b.png] then [c.tiff]. The encoder finishes the
        // first chunk, cancels the run, and the second chunk never starts.
        _batchEncoder.SetupGet(e => e.BatchSize).Returns(2);
        using var cts = new CancellationTokenSource();
        _batchEncoder
            .Setup(e => e.EncodeImagesBatchAsync(It.IsAny<List<string>>(), It.IsAny<IProgress<(int Current, int Total)>>(), It.IsAny<CancellationToken>()))
            .Returns<List<string>, IProgress<(int Current, int Total)>, CancellationToken>((paths, _, ct) =>
            {
                lock (_encodedPaths) { _encodedPaths.AddRange(paths); }
                if (paths.Count == 2)
                {
                    cts.Cancel(); // cancel right after the first chunk is encoded
                    return Task.FromResult(paths.Select(p => ImageVectorFor(p)).ToArray());
                }
                return Task.FromException<float[][]>(new OperationCanceledException());
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.SearchAsync(_tempDir, "autumn", ct: cts.Token));

        // Only the first chunk was encoded...
        Assert.Equal(new[] { "a.jpg", "b.png" }, EncodedNames());
        // ...and its vectors were ALREADY saved to the per-file cache, so the next
        // search of this directory will hit the cache for a.jpg and b.png.
        _store.Verify(s => s.SaveAsync("ClipViTB32", It.IsAny<string>(), It.IsAny<DateTime>(),
            It.IsAny<long>(), It.IsAny<float[]>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SearchAsync_AlreadyCancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // PFA-style cancellation surfaces as OCE or its TaskCanceledException subclass;
        // ThrowsAnyAsync accepts both variants.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.SearchAsync(_tempDir, "autumn", ct: cts.Token));
    }
}