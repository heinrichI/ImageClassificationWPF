using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
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
            new BatchImageEncoderSettings { BatchSize = 32 },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BatchImageEncoder>.Instance);
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
                r => r.ExtractImageToMemoryAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
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

        _mockArchiveReader
            .Setup(r => r.ExtractImageToMemoryAsync(dummyCbz, 0))
            .ReturnsAsync((byte[]?)null);

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
        var progressValues = new List<(int Current, int Total)>();

        try
        {
            await service.SearchAsync(searchDir, "test",
                new Progress<(int Current, int Total)>(p => progressValues.Add(p)));

            Assert.NotEmpty(progressValues);
            Assert.Equal(1, progressValues.Last().Current);
            Assert.Equal(1, progressValues.Last().Total);
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

        return new ComicCoverSearchService(
            _mockArchiveReader.Object,
            _mockClipTextEncoder.Object,
            _mockVectorStore.Object,
            _batchEncoder,
            mockLogger.Object);
    }

    private static float[] CreateEmbedding(float firstValue)
    {
        var vector = new float[512];
        vector[0] = firstValue;
        return vector;
    }
}