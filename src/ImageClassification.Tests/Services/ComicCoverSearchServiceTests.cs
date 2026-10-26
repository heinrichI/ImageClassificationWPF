using ImageClassification.Core.Helpers;
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
    private readonly Mock<IClipImageEncoder> _mockClipImageEncoder;
    private readonly Mock<IVectorStore> _mockVectorStore;
    private readonly Mock<IModelDownloader> _mockDownloader;
    private readonly BatchImageEncoder _batchEncoder;

    public ComicCoverSearchServiceTests()
    {
        _mockArchiveReader = new Mock<IArchiveReader>();
        _mockClipTextEncoder = new Mock<IClipTextEncoder>();
        _mockClipImageEncoder = new Mock<IClipImageEncoder>();
        _mockVectorStore = new Mock<IVectorStore>();
        // The real store migrates the legacy cache on a background thread; tests must not wait.
        _mockVectorStore.SetupGet(v => v.WhenReady).Returns(Task.CompletedTask);
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

        // The cover is cached: a fresh cover row answers the search without opening the archive
        _mockVectorStore
            .Setup(s => s.GetCoverAsync(It.IsAny<string>(), comicPath, It.IsAny<DateTime>(), It.IsAny<long>()))
            .ReturnsAsync(CreateEmbedding(1f));

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
            .Setup(s => s.GetCoverAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<long>()))
            .ReturnsAsync(CreateEmbedding(1f));

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
            .Setup(s => s.GetCoverAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<long>()))
            .ReturnsAsync(CreateEmbedding(1f));

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
            .Setup(s => s.GetCoverAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<long>()))
            .ReturnsAsync(CreateEmbedding(1f));

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
            .Setup(s => s.GetCoverAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<long>()))
            .ReturnsAsync(CreateEmbedding(1f));

        var service = CreateService();
        // SyncProgress (lock-guarded recorder) — the service reports from producer and
        // consumer threads, a plain List would race and lose updates.
        var status = new SyncProgress<ComicSearchStatus>();

        try
        {
            await service.SearchAsync(searchDir, "test", status: status);

            var updates = status.Values;
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
    public async Task SearchAsync_ReportsCurrentFileWhileProcessing()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);

        var cbzFile = Path.Combine(searchDir, "test.cbz");
        await File.WriteAllBytesAsync(cbzFile, []);

        _mockVectorStore
            .Setup(s => s.GetCoverAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<long>()))
            .ReturnsAsync(CreateEmbedding(1f));

        var service = CreateService();
        var status = new SyncProgress<ComicSearchStatus>();

        try
        {
            await service.SearchAsync(searchDir, "test", status: status);

            // The per-archive file report rides the raw channel with its own 250 ms
            // throttle; the first report of a run always passes (never reported before),
            // so the single archive's path must be observed.
            Assert.Contains(status.Values, v => v.CurrentFile == cbzFile);
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

        // The archive row is fresh and holds both pages — nothing must be extracted at all
        _mockVectorStore
            .Setup(s => s.GetArchiveAsync(It.IsAny<string>(), comicPath, It.IsAny<DateTime>(), It.IsAny<long>()))
            .Returns(Task.FromResult<(int, float[][])?>((2, new[] { CreateEmbedding(1f), CreateEmbedding(2f) })));

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

        // A fresh archive row (the whole page array) answers the search without extraction
        _mockVectorStore
            .Setup(s => s.GetArchiveAsync(It.IsAny<string>(), comicPath, It.IsAny<DateTime>(), It.IsAny<long>()))
            .Returns(Task.FromResult<(int, float[][])?>((2, new[] { CreateEmbedding(1f), CreateEmbedding(1f) })));

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
            _mockClipImageEncoder.Object,
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

    [Fact]
    public async Task ExtractPageAsync_ExtractsRequestedPage_NotCover()
    {
        var comicPath = Path.Combine(Path.GetTempPath(), "page_extract_test.cbz");

        var service = CreateService();
        var session = CreateSession(
            ("01.jpg", new byte[] { 1 }),
            ("02.jpg", new byte[] { 2 }),
            ("03.jpg", new byte[] { 3 }));
        _mockArchiveReader
            .Setup(r => r.OpenSessionAsync(comicPath))
            .ReturnsAsync(session.Object);

        // A non-cover page must be extracted, never the cover (page 0)
        var bytes = await service.ExtractPageAsync(comicPath, 2);

        Assert.Equal(new byte[] { 3 }, bytes);
        session.Verify(s => s.ExtractToMemoryAsync(2), Times.Once);
        session.Verify(s => s.ExtractToMemoryAsync(0), Times.Never);
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

        // Mock vector store: no fresh archive/cover rows (cache misses) and accept saves.
        // (The generic GetAsync/SaveAsync mocks below are kept for feature-cache tests
        // that use the same interface; the search service itself only uses the
        // archive/cover APIs.)
        _mockVectorStore
            .Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<long>()))
            .ReturnsAsync((float[]?)null);
        _mockVectorStore
            .Setup(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<long>(), It.IsAny<float[]>()))
            .Returns(Task.CompletedTask);
        _mockVectorStore
            .Setup(s => s.GetArchiveAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<long>()))
            .Returns(Task.FromResult<(int, float[][])?>(null));
        _mockVectorStore
            .Setup(s => s.GetCoverAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<long>()))
            .ReturnsAsync((float[]?)null);
        _mockVectorStore
            .Setup(s => s.SaveArchiveAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<long>(), It.IsAny<float[][]>()))
            .Returns(Task.CompletedTask);
        _mockVectorStore
            .Setup(s => s.SaveCoverAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<long>(), It.IsAny<float[]>()))
            .Returns(Task.CompletedTask);

        // Default archive session: a single page whose extraction yields null —
        // tests that need real results either override the session per path or
        // serve embeddings from the mocked vector store (cache hits).
        _mockArchiveReader
            .Setup(r => r.OpenSessionAsync(It.IsAny<string>()))
            .ReturnsAsync(CreateSession(("page0.jpg", (byte[]?)null)).Object);

        return new ComicCoverSearchService(
            _mockArchiveReader.Object,
            _mockClipTextEncoder.Object,
            _mockClipImageEncoder.Object,
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

    // ─────────────────────── Image-query ("copy the ViT hash") mode ───────────────────────

    /// <summary>Writes a real 1x1 PNG — the query-image pre-check decodes the file with ImageSharp.</summary>
    private static void WriteRealPng(string path)
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(1, 1);
        using var stream = File.Create(path);
        image.Save(stream, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
    }

    [Fact]
    public async Task SearchByArchivePageAsync_CoverMode_CachedCoverVectorIsTheQuery_AndQueryArchiveIsExcluded()
    {
        // Covers mode: the pipeline probes the COVER rows, and the query factory falls
        // back to the cover row for a page-0 query. Everything is answered from the
        // store — "copy the hash" means no archive open and no encoding at all.
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);

        var queryPath = Path.Combine(searchDir, "query_archive.cbz");
        var otherPath = Path.Combine(searchDir, "other_archive.cbz");
        await File.WriteAllBytesAsync(queryPath, []);
        await File.WriteAllBytesAsync(otherPath, []);

        var service = CreateService();

        var queryVector = CreateEmbedding(1f);
        _mockVectorStore
            .Setup(s => s.GetCoverAsync(It.IsAny<string>(), queryPath, It.IsAny<DateTime>(), It.IsAny<long>()))
            .ReturnsAsync(queryVector);
        _mockVectorStore
            .Setup(s => s.GetCoverAsync(It.IsAny<string>(), otherPath, It.IsAny<DateTime>(), It.IsAny<long>()))
            .ReturnsAsync(queryVector); // identical cover vector → 1.0 similarity

        try
        {
            var results = await service.SearchByArchivePageAsync(searchDir, queryPath, 0);

            // The query archive itself is excluded; the other archive (identical cover
            // vector) scores 1.0.
            var other = Assert.Single(results);
            Assert.Equal(otherPath, other.ArchivePath);
            Assert.Equal(1f, other.SimilarityScore, 5);

            _mockArchiveReader.Verify(r => r.OpenSessionAsync(It.IsAny<string>()), Times.Never);
            _mockClipImageEncoder.Verify(e => e.EncodeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockClipTextEncoder.Verify(e => e.EncodeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchAllPagesByArchivePageAsync_ArchiveRowIsTheQuery_AndQueryArchiveIsExcluded()
    {
        // All-pages mode: the pipeline probes the ARCHIVE rows (every page), and the
        // query factory takes the page vector straight from the query archive's row.
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);

        var queryPath = Path.Combine(searchDir, "query_archive.cbz");
        var otherPath = Path.Combine(searchDir, "other_archive.cbz");
        await File.WriteAllBytesAsync(queryPath, []);
        await File.WriteAllBytesAsync(otherPath, []);

        var service = CreateService();

        var queryVector = CreateEmbedding(1f);
        _mockVectorStore
            .Setup(s => s.GetArchiveAsync(It.IsAny<string>(), queryPath, It.IsAny<DateTime>(), It.IsAny<long>()))
            .Returns(Task.FromResult<(int, float[][])?>((1, new[] { queryVector })));
        _mockVectorStore
            .Setup(s => s.GetArchiveAsync(It.IsAny<string>(), otherPath, It.IsAny<DateTime>(), It.IsAny<long>()))
            .Returns(Task.FromResult<(int, float[][])?>((2, new[] { queryVector, queryVector })));

        try
        {
            var results = await service.SearchAllPagesByArchivePageAsync(searchDir, queryPath, 0);

            // The query archive is excluded; the other archive contributes both pages
            // (identical vectors → 1.0 each).
            Assert.Equal(2, results.Count);
            Assert.All(results, r =>
            {
                Assert.Equal(otherPath, r.ArchivePath);
                Assert.Equal(1f, r.SimilarityScore, 5);
            });

            _mockArchiveReader.Verify(r => r.OpenSessionAsync(It.IsAny<string>()), Times.Never);
            _mockClipImageEncoder.Verify(e => e.EncodeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockClipTextEncoder.Verify(e => e.EncodeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchByImageAsync_MissingQueryFile_ReturnsEmptyAndReportsNote()
    {
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);
        await File.WriteAllBytesAsync(Path.Combine(searchDir, "a.cbz"), []);

        var service = CreateService();
        var updates = new System.Collections.Generic.List<ComicSearchStatus>();

        try
        {
            var results = await service.SearchByImageAsync(
                searchDir,
                Path.Combine(searchDir, "no-such.png"),
                new StatusCollector(updates));

            Assert.Empty(results);
            Assert.Contains(updates, u => u.Note is not null && u.Note.Contains("not found"));
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    // ─────────────────────── Hash-query ("search by hash") mode ───────────────────────

    [Fact]
    public async Task SearchByVectorAsync_CoverMode_ScoresCoverRows_AndNeverOpensArchives()
    {
        // The pasted hash is the query itself: everything is answered from the store
        // (cover rows), nothing is encoded, no archive is opened, nothing is excluded.
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);

        var aPath = Path.Combine(searchDir, "a_archive.cbz");
        var bPath = Path.Combine(searchDir, "b_archive.cbz");
        await File.WriteAllBytesAsync(aPath, []);
        await File.WriteAllBytesAsync(bPath, []);

        var service = CreateService();

        _mockVectorStore
            .Setup(s => s.GetCoverAsync(It.IsAny<string>(), aPath, It.IsAny<DateTime>(), It.IsAny<long>()))
            .ReturnsAsync(CreateEmbedding(1f));     // identical to the query → 1.0
        _mockVectorStore
            .Setup(s => s.GetCoverAsync(It.IsAny<string>(), bPath, It.IsAny<DateTime>(), It.IsAny<long>()))
            .ReturnsAsync(CreateEmbedding(0.6f));  // dot product with the query → 0.6

        try
        {
            var results = await service.SearchByVectorAsync(searchDir, CreateEmbedding(1f));

            Assert.Equal(2, results.Count);
            Assert.Equal(aPath, results[0].ArchivePath);
            Assert.Equal(1f, results[0].SimilarityScore, 5);
            Assert.Equal(bPath, results[1].ArchivePath);
            Assert.Equal(0.6f, results[1].SimilarityScore, 5);

            _mockArchiveReader.Verify(r => r.OpenSessionAsync(It.IsAny<string>()), Times.Never);
            _mockClipImageEncoder.Verify(e => e.EncodeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockClipTextEncoder.Verify(e => e.EncodeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task SearchByVectorAsync_EmptyVector_ReportsNoteAndReturnsEmpty()
    {
        var service = CreateService();
        var updates = new System.Collections.Generic.List<ComicSearchStatus>();

        var results = await service.SearchByVectorAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), [], new StatusCollector(updates));

        Assert.Empty(results);
        Assert.Contains("Query hash is empty", Assert.Single(updates).Note);
    }

    [Fact]
    public async Task SearchByVectorAsync_DimensionMismatchWithCache_ReportsNote()
    {
        // A hash pasted from a DIFFERENT model (3 floats vs the 512-float cache rows)
        // must abort with a note instead of scoring garbage (or tripping the Debug
        // dimension assert in VectorMath).
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);
        await File.WriteAllBytesAsync(Path.Combine(searchDir, "a_archive.cbz"), []);

        var service = CreateService();
        _mockVectorStore
            .Setup(s => s.GetCoverAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<long>()))
            .ReturnsAsync(CreateEmbedding(1f)); // 512-d cache rows

        var updates = new System.Collections.Generic.List<ComicSearchStatus>();
        try
        {
            var results = await service.SearchByVectorAsync(searchDir, [1f, 0f, 0f], new StatusCollector(updates));

            Assert.Empty(results);
            Assert.Contains("different model", updates[^1].Note);
            _mockArchiveReader.Verify(r => r.OpenSessionAsync(It.IsAny<string>()), Times.Never);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    [Fact]
    public async Task GetPageHashAsync_ReturnsBase64OfArchivedPage_AndNeverOpensArchive()
    {
        // "Copy hash" on a tile: the page vector comes straight from the store row.
        var searchDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(searchDir);

        var path = Path.Combine(searchDir, "a_archive.cbz");
        await File.WriteAllBytesAsync(path, []);

        var service = CreateService();
        _mockVectorStore
            .Setup(s => s.GetArchiveAsync(It.IsAny<string>(), path, It.IsAny<DateTime>(), It.IsAny<long>()))
            .Returns(Task.FromResult<(int, float[][])?>((2, new[] { CreateEmbedding(1f), CreateEmbedding(2f) })));

        try
        {
            var hash = await service.GetPageHashAsync(path, 0);

            Assert.NotNull(hash);
            Assert.True(VectorCodec.TryParseBase64(hash, out var parsed));
            Assert.Equal(CreateEmbedding(1f), parsed);

            _mockArchiveReader.Verify(r => r.OpenSessionAsync(It.IsAny<string>()), Times.Never);
        }
        finally
        {
            Directory.Delete(searchDir, true);
        }
    }

    /// <summary>Collects status updates for assertions (IProgress<T> is fire-and-forget).</summary>
    private sealed class StatusCollector : System.IProgress<ComicSearchStatus>
    {
        private readonly object _lock = new();
        private readonly System.Collections.Generic.List<ComicSearchStatus> _list;

        public StatusCollector(System.Collections.Generic.List<ComicSearchStatus> list)
        {
            _list = list;
        }

        public void Report(ComicSearchStatus value)
        {
            lock (_lock) _list.Add(value);
        }
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