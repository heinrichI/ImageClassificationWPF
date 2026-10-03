using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
using ImageClassification.Tests;
using Microsoft.Extensions.Logging;
using Moq;

namespace ImageClassification.Tests.Services;

public class ComicCoverSearchServiceTests
{
    private readonly Mock<IArchiveReader> _mockArchiveReader;
    private readonly Mock<IClipTextEncoder> _mockClipTextEncoder;
    private readonly Mock<IVectorStore> _mockVectorStore;
    private readonly Mock<IModelDownloader> _mockDownloader;
    private readonly BatchImageEncoder _batchEncoder;

    public ComicCoverSearchServiceTests()
    {
        _mockArchiveReader = new Mock<IArchiveReader>();
        _mockClipTextEncoder = new Mock<IClipTextEncoder>();
        _mockVectorStore = new Mock<IVectorStore>();
        _mockDownloader = new Mock<IModelDownloader>();
        // Point to a non-existent path — tests that don't invoke inference won't load the model
        _mockDownloader.Setup(d => d.GetModelPath(It.IsAny<string>())).Returns("dummy.onnx");
        _batchEncoder = new BatchImageEncoder(
            _mockDownloader.Object,
            new FakeGpuPipelineOptions { BatchSize = 32 },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BatchImageEncoder>.Instance,
            new FakeOnnxRuntimeOptions());
    }

    [Fact]
    public async Task SearchAsync_NoArchivesFound_ReturnsEmptyList()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);

        var service = CreateService();

        try
        {
            var results = await service.SearchAsync(searchDir, "beach");

            Assert.NotNull(results);
            Assert.Empty(results);
            _mockArchiveReader.Verify(
                r => r.OpenSessionAsync(It.IsAny<string>()), Times.Never);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_DirectoryNotFound_ReturnsEmptyList()
    {
        var service = CreateService();
        var nonExistentDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var results = await service.SearchAsync(nonExistentDir, "test");

        Assert.NotNull(results);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_ArchiveReaderReturnsNull_SkipsArchive()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);

        var dummyCbz = Path.Combine(searchDir, "test.cbz");
        await File.WriteAllBytesAsync(dummyCbz, []);

        // Default session (see CreateService): one page whose extraction yields null
        var service = CreateService();

        try
        {
            var results = await service.SearchAsync(searchDir, "beach");

            Assert.NotNull(results);
            Assert.Empty(results);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_ReturnsResultForArchive()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);

        var comicPath = Path.Combine(searchDir, "summer_adventure.cbz");
        await File.WriteAllBytesAsync(comicPath, []);

        var service = CreateService();

        _mockVectorStore
            .Setup(s => s.GetBatchAsync(It.IsAny<string>(),
                It.Is<List<(string FilePath, DateTime LastModified, long FileSize)>>(l =>
                    l.Any(x => x.FilePath == comicPath))))
            .ReturnsAsync((string _, List<(string FilePath, DateTime LastModified, long FileSize)> entries) =>
            {
                var result = new Dictionary<string, float[]?>();
                foreach (var (path, _, _) in entries)
                    result[path] = path == comicPath ? CreateEmbedding(1f) : null;
                return result;
            });

        try
        {
            var results = await service.SearchAsync(searchDir, "summer");

            Assert.NotNull(results);
            var coverResult = Assert.Single(results);
            Assert.Equal(comicPath, coverResult.ArchivePath);
            Assert.Equal("summer_adventure.cbz", coverResult.ArchiveFileName);
            Assert.True(coverResult.SimilarityScore > 0);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_SubdirectoryArchives_AreScanned()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var subDir = Path.Combine(searchDir, "subfolder");
        Directory.CreateDirectory(subDir);

        var comicPath = Path.Combine(subDir, "nested.cbr");
        await File.WriteAllBytesAsync(comicPath, []);

        var service = CreateService();

        _mockVectorStore
            .Setup(s => s.GetBatchAsync(It.IsAny<string>(),
                It.IsAny<List<(string FilePath, DateTime LastModified, long FileSize)>>()))
            .ReturnsAsync((string _, List<(string FilePath, DateTime LastModified, long FileSize)> entries) =>
            {
                var result = new Dictionary<string, float[]?>();
                foreach (var (path, _, _) in entries)
                    result[path] = CreateEmbedding(1f);
                return result;
            });

        try
        {
            var results = await service.SearchAsync(searchDir, "summer");

            Assert.NotNull(results);
            var coverResult = Assert.Single(results);
            Assert.Equal("nested.cbr", coverResult.ArchiveFileName);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_MultipleArchives_ReturnsAllResults()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);

        var matchPath = Path.Combine(searchDir, "match.cbz");
        var skipPath = Path.Combine(searchDir, "skip.cbr");
        await File.WriteAllBytesAsync(matchPath, []);
        await File.WriteAllBytesAsync(skipPath, []);

        var service = CreateService();

        _mockVectorStore
            .Setup(s => s.GetBatchAsync(It.IsAny<string>(),
                It.IsAny<List<(string FilePath, DateTime LastModified, long FileSize)>>()))
            .ReturnsAsync((string _, List<(string FilePath, DateTime LastModified, long FileSize)> entries) =>
            {
                var result = new Dictionary<string, float[]?>();
                foreach (var (path, _, _) in entries)
                    result[path] = CreateEmbedding(1f);
                return result;
            });

        try
        {
            var results = await service.SearchAsync(searchDir, "beach");

            Assert.NotNull(results);
            Assert.Equal(2, results.Count);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_IgnoresNonArchiveFiles()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);

        var txtFile = Path.Combine(searchDir, "readme.txt");
        var cbzFile = Path.Combine(searchDir, "comic.cbz");
        await File.WriteAllTextAsync(txtFile, "hello");
        await File.WriteAllBytesAsync(cbzFile, []);

        var service = CreateService();

        _mockVectorStore
            .Setup(s => s.GetBatchAsync(It.IsAny<string>(),
                It.IsAny<List<(string FilePath, DateTime LastModified, long FileSize)>>()))
            .ReturnsAsync((string _, List<(string FilePath, DateTime LastModified, long FileSize)> entries) =>
            {
                var result = new Dictionary<string, float[]?>();
                foreach (var (path, _, _) in entries)
                    result[path] = CreateEmbedding(1f);
                return result;
            });

        try
        {
            var results = await service.SearchAsync(searchDir, "test");

            Assert.NotNull(results);
            var coverResult = Assert.Single(results);
            Assert.Equal("comic.cbz", coverResult.ArchiveFileName);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_ReportsProgress()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);

        var cbzFile = Path.Combine(searchDir, "test.cbz");
        await File.WriteAllBytesAsync(cbzFile, []);

        _mockVectorStore
            .Setup(s => s.GetBatchAsync(It.IsAny<string>(),
                It.IsAny<List<(string FilePath, DateTime LastModified, long FileSize)>>()))
            .ReturnsAsync((string _, List<(string FilePath, DateTime LastModified, long FileSize)> entries) =>
            {
                var result = new Dictionary<string, float[]?>();
                foreach (var (path, _, _) in entries)
                    result[path] = CreateEmbedding(1f);
                return result;
            });

        var service = CreateService();
        var updates = new List<ComicSearchStatus>();

        try
        {
            await service.SearchAsync(searchDir, "test",
                new Progress<ComicSearchStatus>(p => updates.Add(p)));

            Assert.NotEmpty(updates);
            Assert.Equal(1, updates.Last().Current);
            Assert.Equal(1, updates.Last().Total);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_ReportsStatusPhases_InOrder()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);
        await File.WriteAllBytesAsync(Path.Combine(searchDir, "test.cbz"), []);

        var service = CreateService();
        var status = new SyncProgress<ComicSearchStatus>();

        try
        {
            await service.SearchAsync(searchDir, "test", status: status);

            // Phase order: covers mode — same five phases as all-pages
            // (no separate page-count / cache-check phases anymore).
            // Detail lines and counter ticks carry an empty Phase and are skipped.
            var phases = status.Values.Where(v => v.Phase.Length > 0).Select(v => v.Phase).ToList();
            string[] expected =
            {
                "Scanning archives...",
                "Found 1 archive",
                "Encoding query text...",
                "Processing archives...",
                "Scoring and ranking results..."
            };

            int last = -1;
            foreach (var phase in expected)
            {
                var idx = phases.IndexOf(phase);
                Assert.True(idx > last,
                    $"Phase '{phase}' expected after index {last}. Actual sequence: {string.Join(" | ", phases)}");
                last = idx;
            }
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAllPagesAsync_OpensArchiveOnce_PerArchive()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);

        var comicPath = Path.Combine(searchDir, "test.cbz");
        await File.WriteAllBytesAsync(comicPath, []);

        var service = CreateService();

        // Three pages, all extraction misses — the archive must still be opened
        // exactly ONCE and every page extracted from that open session.
        _mockArchiveReader
            .Setup(r => r.OpenSessionAsync(comicPath))
            .ReturnsAsync(CreateSession(("01.jpg", (byte[]?)null), ("02.jpg", null), ("03.jpg", null)).Object);

        try
        {
            await service.SearchAllPagesAsync(searchDir, "test");

            _mockArchiveReader.Verify(r => r.OpenSessionAsync(comicPath), Times.Once);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAllPagesAsync_ColdArchive_UsesBulkExtraction()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);

        var comicPath = Path.Combine(searchDir, "test.cbz");
        await File.WriteAllBytesAsync(comicPath, []);

        var service = CreateService();
        var session = CreateSession(("01.jpg", (byte[]?)null), ("02.jpg", null), ("03.jpg", null));
        _mockArchiveReader
            .Setup(r => r.OpenSessionAsync(comicPath))
            .ReturnsAsync(session.Object);
        // Default vector store returns null for every key — every page is a cache miss

        try
        {
            await service.SearchAllPagesAsync(searchDir, "test");

            // More than half of the pages are misses → one bulk pass, no per-page extraction
            session.Verify(s => s.ExtractAllToMemoryAsync(), Times.Once);
            session.Verify(s => s.ExtractToMemoryAsync(It.IsAny<int>()), Times.Never);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAllPagesAsync_WarmArchive_SkipsBulkExtraction()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);

        var comicPath = Path.Combine(searchDir, "test.cbz");
        await File.WriteAllBytesAsync(comicPath, []);

        var service = CreateService();
        var session = CreateSession(("01.jpg", (byte[]?)null), ("02.jpg", null));
        _mockArchiveReader
            .Setup(r => r.OpenSessionAsync(comicPath))
            .ReturnsAsync(session.Object);

        // Both pages are in the embedding cache — nothing must be extracted at all
        _mockVectorStore
            .Setup(s => s.GetBatchAsync(It.IsAny<string>(),
                It.IsAny<List<(string FilePath, DateTime LastModified, long FileSize)>>()))
            .ReturnsAsync((string _, List<(string FilePath, DateTime LastModified, long FileSize)> entries) =>
            {
                var result = new Dictionary<string, float[]?>();
                foreach (var (path, _, _) in entries)
                    result[path] = CreateEmbedding(1f);
                return result;
            });

        try
        {
            var results = await service.SearchAllPagesAsync(searchDir, "test");

            Assert.Equal(2, results.Count);
            session.Verify(s => s.ExtractAllToMemoryAsync(), Times.Never);
            session.Verify(s => s.ExtractToMemoryAsync(It.IsAny<int>()), Times.Never);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_CoversMode_SkipsBulkExtraction()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);

        var comicPath = Path.Combine(searchDir, "test.cbz");
        await File.WriteAllBytesAsync(comicPath, []);

        var service = CreateService();
        var session = CreateSession(("01.jpg", (byte[]?)null), ("02.jpg", null), ("03.jpg", null));
        _mockArchiveReader
            .Setup(r => r.OpenSessionAsync(comicPath))
            .ReturnsAsync(session.Object);
        // The cover (page 0) is a cache miss

        try
        {
            await service.SearchAsync(searchDir, "test");

            // Covers mode touches only page 0 — the bulk pass must not trigger
            session.Verify(s => s.ExtractAllToMemoryAsync(), Times.Never);
            session.Verify(s => s.ExtractToMemoryAsync(0), Times.Once);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAllPagesAsync_ExactlyHalfMisses_UsesPerPageExtraction()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);

        var comicPath = Path.Combine(searchDir, "test.cbz");
        await File.WriteAllBytesAsync(comicPath, []);

        var service = CreateService();
        var session = CreateSession(
            ("01.jpg", (byte[]?)null), ("02.jpg", null), ("03.jpg", null), ("04.jpg", null));
        _mockArchiveReader
            .Setup(r => r.OpenSessionAsync(comicPath))
            .ReturnsAsync(session.Object);

        // Pages 0 and 1 are cached, pages 2 and 3 are misses — exactly half, so no bulk pass
        _mockVectorStore
            .Setup(s => s.GetBatchAsync(It.IsAny<string>(),
                It.IsAny<List<(string FilePath, DateTime LastModified, long FileSize)>>()))
            .ReturnsAsync((string _, List<(string FilePath, DateTime LastModified, long FileSize)> entries) =>
            {
                var result = new Dictionary<string, float[]?>();
                foreach (var (path, _, _) in entries)
                    result[path] = path.EndsWith("|page=0", StringComparison.Ordinal)
                        || path.EndsWith("|page=1", StringComparison.Ordinal)
                        ? CreateEmbedding(1f)
                        : null;
                return result;
            });

        try
        {
            await service.SearchAllPagesAsync(searchDir, "test");

            session.Verify(s => s.ExtractAllToMemoryAsync(), Times.Never);
            session.Verify(s => s.ExtractToMemoryAsync(It.IsAny<int>()), Times.Exactly(2));
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAllPagesAsync_CachedPagesProduceResultsWithPageCount()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);

        var comicPath = Path.Combine(searchDir, "test.cbz");
        await File.WriteAllBytesAsync(comicPath, []);

        var service = CreateService();

        _mockArchiveReader
            .Setup(r => r.OpenSessionAsync(comicPath))
            .ReturnsAsync(CreateSession(("01.jpg", (byte[]?)null), ("02.jpg", null)).Object);

        // Both pages are in the embedding cache (keys "{path}|page=N") — no extraction needed
        _mockVectorStore
            .Setup(s => s.GetBatchAsync(It.IsAny<string>(),
                It.IsAny<List<(string FilePath, DateTime LastModified, long FileSize)>>()))
            .ReturnsAsync((string _, List<(string FilePath, DateTime LastModified, long FileSize)> entries) =>
            {
                var result = new Dictionary<string, float[]?>();
                foreach (var (path, _, _) in entries)
                    result[path] = path.StartsWith($"{comicPath}|page=", StringComparison.Ordinal)
                        ? CreateEmbedding(1f)
                        : null;
                return result;
            });

        try
        {
            var results = await service.SearchAllPagesAsync(searchDir, "test");

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.Equal(2, r.PageCount));
            Assert.Contains(results, r => r.PageIndex == 0);
            Assert.Contains(results, r => r.PageIndex == 1);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAllPagesAsync_ReportsStatusPhases_InOrder()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);
        await File.WriteAllBytesAsync(Path.Combine(searchDir, "test.cbz"), []);

        var service = CreateService();

        // Two pages; extraction yields nothing (cache misses) — phases must still be reported
        _mockArchiveReader
            .Setup(r => r.OpenSessionAsync(It.IsAny<string>()))
            .ReturnsAsync(CreateSession(("01.jpg", (byte[]?)null), ("02.jpg", null)).Object);

        var status = new SyncProgress<ComicSearchStatus>();

        try
        {
            await service.SearchAllPagesAsync(searchDir, "test", status: status);

            // Phases must arrive in pipeline order (no separate page-count phase);
            // detail lines and counter ticks carry an empty Phase and are skipped
            var phases = status.Values.Where(v => v.Phase.Length > 0).Select(v => v.Phase).ToList();
            string[] expected =
            {
                "Scanning archives...",
                "Found 1 archive",
                "Encoding query text...",
                "Processing archives...",
                "Scoring and ranking results..."
            };

            int last = -1;
            foreach (var phase in expected)
            {
                var idx = phases.IndexOf(phase);
                Assert.True(idx > last,
                    $"Phase '{phase}' expected after index {last}. Actual sequence: {string.Join(" | ", phases)}");
                last = idx;
            }
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_ReportsGpuQueueDetailInStatus()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);
        await File.WriteAllBytesAsync(Path.Combine(searchDir, "test.cbz"), []);

        var service = CreateService();
        var status = new SyncProgress<ComicSearchStatus>();

        try
        {
            await service.SearchAsync(searchDir, "test", status: status);

            // A detail update with the GPU-queue state must be reported during processing
            var detail = status.Values.FirstOrDefault(v => v.Detail is not null);
            Assert.NotNull(detail);
            Assert.Contains("GPU", detail!.Detail);
            Assert.Contains("queue", detail.Detail);
            Assert.NotNull(detail.GpuQueueDepth);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_LogsCacheSummaryLine()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);
        await File.WriteAllBytesAsync(Path.Combine(searchDir, "test.cbz"), []);

        // Set up the shared mocks (via CreateService), then build a service
        // with a capturing logger to inspect the formatted log lines
        CreateService();
        var logger = new CapturingLogger();
        var service = new ComicCoverSearchService(
            _mockArchiveReader.Object,
            _mockClipTextEncoder.Object,
            _mockVectorStore.Object,
            _batchEncoder,
            logger);

        try
        {
            await service.SearchAsync(searchDir, "test");

            Assert.Contains(logger.Messages, m =>
                m.StartsWith("[Pipeline] Cache summary:", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_OpenFailureNote_IsReportedToStatusChannel()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);
        string comicPath = Path.Combine(searchDir, "junk.cbr");
        await File.WriteAllBytesAsync(comicPath, new byte[] { 1, 2, 3 });

        var service = CreateService();

        // The session opens with zero pages and carries an open-stage diagnostic —
        // exactly the shape ArchiveReader produces when the 7z layer refuses the
        // container ("not a known archive type"). The note must reach the UI.
        const string note = "junk.cbr is not a known archive type — skipped as empty";
        var session = CreateSession(); // zero pages
        session.Setup(s => s.LastNote).Returns(note);
        _mockArchiveReader
            .Setup(r => r.OpenSessionAsync(comicPath))
            .ReturnsAsync(session.Object);

        var status = new SyncProgress<ComicSearchStatus>();

        try
        {
            await service.SearchAsync(searchDir, "test", status: status);

            Assert.Contains(status.Values, v => v.Note == note);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAsync_OpenMismatchNote_IsReportedExactlyOnce_InCoversMode()
    {
        // Covers mode: pageEnd == 1, the bulk path never runs — the open-stage
        // mismatch note must still reach the UI, and exactly once.
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);
        string comicPath = Path.Combine(searchDir, "renamed.cbr");
        await File.WriteAllBytesAsync(comicPath, new byte[] { 1, 2, 3 });

        var service = CreateService();

        const string note = "renamed.cbr: ZIP container inside '.cbr' — opened with ZIP handler";
        var session = CreateSession(("page0.jpg", (byte[]?)null)); // one page → no bulk
        session.Setup(s => s.LastNote).Returns(note);
        _mockArchiveReader
            .Setup(r => r.OpenSessionAsync(comicPath))
            .ReturnsAsync(session.Object);

        var status = new SyncProgress<ComicSearchStatus>();

        try
        {
            await service.SearchAsync(searchDir, "test", status: status);

            Assert.Equal(1, status.Values.Count(v => v.Note == note));
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    private ComicCoverSearchService CreateService()
    {
        var mockLogger = new Mock<ILogger<ComicCoverSearchService>>();

        // Mock text encoder to return a deterministic vector
        _mockClipTextEncoder
            .Setup(e => e.EncodeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string text, CancellationToken _) =>
            {
                var vec = new float[512];
                vec[0] = 1.0f;
                return vec;
            });

        // Mock vector store to return null (cache miss) and accept saves
        _mockVectorStore
            .Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<long>()))
            .ReturnsAsync((float[]?)null);
        _mockVectorStore
            .Setup(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<long>(), It.IsAny<float[]>()))
            .Returns(Task.CompletedTask);
        _mockVectorStore
            .Setup(s => s.GetBatchAsync(It.IsAny<string>(),
                It.IsAny<List<(string FilePath, DateTime LastModified, long FileSize)>>()))
            .Returns((string modelName,
                List<(string FilePath, DateTime LastModified, long FileSize)> entries) =>
            {
                var result = new Dictionary<string, float[]?>();
                foreach (var (path, _, _) in entries)
                    result[path] = null;
                return Task.FromResult(result);
            });

        // Default archive session: a single page whose extraction yields null —
        // tests that need real results either override the session per path or
        // serve embeddings from the mocked vector store (cache hits).
        _mockArchiveReader
            .Setup(r => r.OpenSessionAsync(It.IsAny<string>()))
            .ReturnsAsync(CreateSession(("page0.jpg", (byte[]?)null)).Object);

        return new ComicCoverSearchService(
            _mockArchiveReader.Object,
            _mockClipTextEncoder.Object,
            _mockVectorStore.Object,
            _batchEncoder,
            mockLogger.Object);
    }

    /// <summary>
    /// Builds a mocked archive session with the given pages (name + extracted bytes;
    /// null bytes simulate extraction failure). Bulk extraction is stubbed to return
    /// the same bytes in entry order. Returns the Mock so callers can verify calls.
    /// </summary>
    private static Mock<IArchiveSession> CreateSession(params (string Name, byte[]? Bytes)[] pages)
    {
        var session = new Mock<IArchiveSession>();
        session.Setup(s => s.ImageEntries)
            .Returns(pages.Select(p => p.Name).ToList());
        session.Setup(s => s.ExtractToMemoryAsync(It.IsAny<int>()))
            .ReturnsAsync((int index) => index >= 0 && index < pages.Length ? pages[index].Bytes : null);
        session.Setup(s => s.ExtractAllToMemoryAsync())
            .ReturnsAsync(pages.Select(p => p.Bytes).ToArray());
        return session;
    }

    private static float[] CreateEmbedding(float firstValue)
    {
        var vector = new float[512];
        vector[0] = firstValue;
        return vector;
    }

    /// <summary>
    /// Minimal ILogger that records formatted messages (Information and above)
    /// for assertions on log content.
    /// </summary>
    private sealed class CapturingLogger : ILogger<ComicCoverSearchService>
    {
        private readonly object _lock = new();
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            lock (_lock) Messages.Add(formatter(state, exception));
        }
    }

    /// <summary>
    /// Deterministic IProgress that records values synchronously on the reporting
    /// thread (unlike Progress&lt;T&gt; which posts to a SynchronizationContext).
    /// Thread-safe: the service may report from producer (thread-pool) and
    /// consumer (calling) threads concurrently.
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly object _lock = new();
        public List<T> Values { get; } = new();

        public void Report(T value)
        {
            lock (_lock) Values.Add(value);
        }
    }
}