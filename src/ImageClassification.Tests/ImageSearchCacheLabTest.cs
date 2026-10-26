using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
using ImageClassification.VectorStore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageClassification.Tests;

/// <summary>
/// Lab test — the definitive proof (or reproduction) that the per-file CLIP cache works
/// across store instances, i.e. across app runs:
///   run 1 (fresh DB)  → all files are misses → the fake encoder encodes all 3, rows saved
///   run 2 (same DB)   → all files must be cache hits → the encoder is NEVER called
///   run 3 (b.png modified) → exactly b.png is stale and re-encoded; a/c stay cached
/// Uses the REAL SqliteVectorStore and the REAL ImageSearchService; only the CLIP
/// encoders are faked (no ONNX model needed in tests).
/// </summary>
public class ImageSearchCacheLabTest
{
    private static readonly float[] QueryVector = [1f, 0f, 0f, 0f];

    [Fact]
    public async Task RealStore_ThreeRuns_CacheHitsOnSecondRun_AndStaleOnModifiedFile()
    {
        var dir = Directory.CreateTempSubdirectory("img-lab-").FullName;
        var enc1 = new FakeBatchEncoder();
        var enc2 = new FakeBatchEncoder();
        var enc3 = new FakeBatchEncoder();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.jpg"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(dir, "b.png"), new byte[] { 2, 3 });
            File.WriteAllBytes(Path.Combine(dir, "c.tiff"), new byte[] { 4, 5, 6 });
            var dbPath = Path.Combine(dir, "vector_cache.db");

            // Run 1: fresh DB — everything is a miss.
            var r1 = await RunOnce(dbPath, dir, enc1);
            Assert.Equal(3, r1.Count);
            Assert.Equal(3, enc1.Encoded.Count);

            // Run 2: same DB file, new store instance (≈ app restart) — full cache.
            var r2 = await RunOnce(dbPath, dir, enc2);
            Assert.Equal(3, r2.Count);
            Assert.Empty(enc2.Encoded); // ← the key assertion: nothing re-encoded

            // Run 3: b.png changed (new content → new mtime + size) → exactly one re-encode.
            File.WriteAllBytes(Path.Combine(dir, "b.png"), new byte[] { 9, 9, 9, 9 });
            var r3 = await RunOnce(dbPath, dir, enc3);
            Assert.Equal(3, r3.Count);
            Assert.Equal(1, enc3.Encoded.Count);
            Assert.EndsWith("b.png", enc3.Encoded[0]);
        }
        finally
        {
            // Best-effort: the store's background migration task can still hold a file
            // handle for a moment; retry, then give up (OS cleans up temp dirs).
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    break;
                }
                catch
                {
                    Thread.Sleep(200);
                }
            }
        }
    }

    private static async Task<List<ImageSearchResult>> RunOnce(
        string dbPath, string dir, FakeBatchEncoder encoder)
    {
        using var store = new SqliteVectorStore(dbPath);
        var service = new ImageSearchService(
            new FakeTextEncoder(), Moq.Mock.Of<ImageClassification.Core.Services.IClipImageEncoder>(), encoder, store, NullLogger<ImageSearchService>.Instance);

        return await service.SearchAsync(dir, "autumn");
    }

    private static float[] VectorFor(string fileName) => fileName switch
    {
        "a.jpg" => [1f, 0f, 0f, 0f],
        "b.png" => [0.6f, 0.8f, 0f, 0f],
        "c.tiff" => [0f, 1f, 0f, 0f],
        _ => [0f, 0f, 0f, 0f]
    };

    private sealed class FakeTextEncoder : IClipTextEncoder
    {
        public Task<float[]> EncodeTextAsync(string text, CancellationToken ct = default)
            => Task.FromResult(QueryVector);

        public void Dispose() { }
    }

    private sealed class FakeBatchEncoder : IBatchImageEncoder
    {
        public int BatchSize { get; set; } = 32;
        public int PrefetchCount { get; set; } = 2;
        public List<string> Encoded { get; } = new();

        public Task<float[][]> EncodeImagesBatchAsync(
            List<string> imagePaths,
            IProgress<(int Current, int Total)>? progress = null,
            CancellationToken ct = default)
        {
            lock (Encoded)
            {
                Encoded.AddRange(imagePaths);
            }
            progress?.Report((imagePaths.Count, imagePaths.Count));
            return Task.FromResult(imagePaths.Select(p => VectorFor(Path.GetFileName(p))).ToArray());
        }

        public Task<float[][]> EncodeImagesFromMemoryBatchAsync(
            List<(int OriginalIndex, byte[] Data)> items,
            IProgress<(int Current, int Total)>? progress = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Dispose() { }
    }
}