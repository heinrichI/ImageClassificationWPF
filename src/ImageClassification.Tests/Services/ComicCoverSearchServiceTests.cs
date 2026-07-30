using ImageClassification.ArchiveReader;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ImageClassification.Tests.Services;

public class ComicCoverSearchServiceTests
{
    private readonly Mock<IArchiveReader> _mockArchiveReader;
    private readonly Mock<IClipImageEncoder> _mockClipImageEncoder;
    private readonly Mock<IClipTextEncoder> _mockClipTextEncoder;
    private readonly Mock<IVectorStore> _mockVectorStore;

    public ComicCoverSearchServiceTests()
    {
        _mockArchiveReader = new Mock<IArchiveReader>();
        _mockClipImageEncoder = new Mock<IClipImageEncoder>();
        _mockClipTextEncoder = new Mock<IClipTextEncoder>();
        _mockVectorStore = new Mock<IVectorStore>();
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
                r => r.ExtractFirstImageAsync(It.IsAny<string>()), Times.Never);
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
            .Setup(r => r.ExtractFirstImageAsync(dummyCbz))
            .ReturnsAsync((string?)null);

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
        var coverPath = Path.Combine(searchDir, "cover_tmp.jpg");
        await File.WriteAllTextAsync(coverPath, "fake-image-data");

        _mockArchiveReader
            .Setup(r => r.ExtractFirstImageAsync(comicPath))
            .ReturnsAsync(coverPath);

        var service = CreateService();

        try
        {
            var results = await service.SearchAsync(searchDir, "summer");

            Assert.NotNull(results);
            var coverResult = Assert.Single(results);
            Assert.Equal(comicPath, coverResult.ArchivePath);
            Assert.Equal("summer_adventure.cbz", coverResult.ArchiveFileName);
            Assert.Equal(coverPath, coverResult.CoverImagePath);
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
        var coverPath = Path.Combine(subDir, "cover_tmp.jpg");
        await File.WriteAllTextAsync(coverPath, "fake-image-data");

        _mockArchiveReader
            .Setup(r => r.ExtractFirstImageAsync(comicPath))
            .ReturnsAsync(coverPath);

        var service = CreateService();

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
        var cover1 = Path.Combine(searchDir, "cover1.jpg");
        var cover2 = Path.Combine(searchDir, "cover2.jpg");
        await File.WriteAllBytesAsync(matchPath, []);
        await File.WriteAllBytesAsync(skipPath, []);
        await File.WriteAllTextAsync(cover1, "cover1");
        await File.WriteAllTextAsync(cover2, "cover2");

        _mockArchiveReader
            .Setup(r => r.ExtractFirstImageAsync(matchPath)).ReturnsAsync(cover1);
        _mockArchiveReader
            .Setup(r => r.ExtractFirstImageAsync(skipPath)).ReturnsAsync(cover2);

        var service = CreateService();

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
        var coverPath = Path.Combine(searchDir, "cover.jpg");
        await File.WriteAllTextAsync(txtFile, "hello");
        await File.WriteAllBytesAsync(cbzFile, []);
        await File.WriteAllTextAsync(coverPath, "cover");

        _mockArchiveReader
            .Setup(r => r.ExtractFirstImageAsync(cbzFile)).ReturnsAsync(coverPath);

        var service = CreateService();

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
        var coverPath = Path.Combine(searchDir, "cover.jpg");
        await File.WriteAllBytesAsync(cbzFile, []);
        await File.WriteAllTextAsync(coverPath, "cover");

        _mockArchiveReader
            .Setup(r => r.ExtractFirstImageAsync(cbzFile)).ReturnsAsync(coverPath);

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
        // Use a uniform vector so cosine similarity is always positive
        _mockClipTextEncoder
            .Setup(e => e.EncodeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string text, CancellationToken _) =>
            {
                var vec = new float[512];
                vec[0] = 1.0f;
                return vec;
            });

        // Mock image encoder to return a deterministic vector
        // Use the same index as text encoder so cosine similarity = 1.0
        _mockClipImageEncoder
            .Setup(e => e.EncodeImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) =>
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

        return new ComicCoverSearchService(
            _mockArchiveReader.Object,
            _mockClipImageEncoder.Object,
            _mockClipTextEncoder.Object,
            _mockVectorStore.Object,
            mockLogger.Object);
    }
}