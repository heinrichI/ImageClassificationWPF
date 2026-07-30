using ImageClassification.Core.Services;
using ImageClassification.VectorStore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImageClassification.Tests.Services;

/// <summary>
/// Integration test that runs the full comic cover search pipeline
/// against a real .cbr archive and the CLIP ONNX model.
/// Skipped by default — run manually with:
/// dotnet test --filter "FullyQualifiedName~ComicCoverSearchIntegrationTest"
/// </summary>
public sealed class ComicCoverSearchIntegrationTest : IDisposable
{
    private readonly ImageClassification.ArchiveReader.ArchiveReader _archiveReader;
    private readonly ILogger<ComicCoverSearchService> _logger;
    private readonly ModelDownloader _downloader;
    private readonly ClipImageEncoder _clipImageEncoder;
    private readonly ClipTextEncoder _clipTextEncoder;
    private readonly SqliteVectorStore _vectorStore;

    public ComicCoverSearchIntegrationTest()
    {
        _archiveReader = new ImageClassification.ArchiveReader.ArchiveReader();
        _logger = NullLogger<ComicCoverSearchService>.Instance;
        _downloader = new ModelDownloader();
        _clipImageEncoder = new ClipImageEncoder(_downloader);
        _clipTextEncoder = new ClipTextEncoder(_downloader);
        _vectorStore = new SqliteVectorStore();
    }

    [Fact(Skip = "Integration test — requires real CLIP ONNX model, BPE vocabulary, and .cbr file")]
    public async Task SearchAsync_RealCbrFile_ReturnsCoverWithScore()
    {
        // Arrange
        var comicPath = @"f:\E\SourceC#My\TestComicContainer\Witchblade - Red Sonja 05 24.cbr";

        Assert.True(File.Exists(comicPath), $"Comic file not found: {comicPath}");

        var service = new ComicCoverSearchService(
            _archiveReader, _clipImageEncoder, _clipTextEncoder, _vectorStore, _logger);

        var dir = Path.GetDirectoryName(comicPath)!;

        // Act
        var results = await service.SearchAsync(dir, "witchblade red sonja");

        // Assert
        Assert.NotNull(results);
        Assert.NotEmpty(results);

        // The directory may contain multiple .cbr files; verify the target file is among results
        var witchbladeResult = results.FirstOrDefault(r =>
            r.ArchiveFileName == "Witchblade - Red Sonja 05 24.cbr");
        Assert.NotNull(witchbladeResult);
        Assert.NotNull(witchbladeResult.CoverImagePath);
        Assert.True(File.Exists(witchbladeResult.CoverImagePath),
            $"Cover temp file does not exist: {witchbladeResult.CoverImagePath}");

        // Verify the score is a valid cosine similarity (0..1 range)
        Assert.True(witchbladeResult.SimilarityScore > 0,
            $"Expected positive similarity score but got {witchbladeResult.SimilarityScore}");
        Assert.True(witchbladeResult.SimilarityScore <= 1.0f,
            $"Expected similarity score <= 1.0 but got {witchbladeResult.SimilarityScore}");

        // Verify the cover image has actual content
        var fileInfo = new FileInfo(witchbladeResult.CoverImagePath);
        Assert.True(fileInfo.Length > 0, "Cover image is empty");
    }

    [Fact(Skip = "Integration test — requires real CLIP ONNX model and .cbr file")]
    public async Task SearchAsync_RealCbrFile_DifferentQuery_ReturnsLowerScore()
    {
        // Arrange
        var comicPath = @"f:\E\SourceC#My\TestComicContainer\Witchblade - Red Sonja 05 24.cbr";

        Assert.True(File.Exists(comicPath), $"Comic file not found: {comicPath}");

        var service = new ComicCoverSearchService(
            _archiveReader, _clipImageEncoder, _clipTextEncoder, _vectorStore, _logger);

        var dir = Path.GetDirectoryName(comicPath)!;

        // Act
        var relevantResults = await service.SearchAsync(dir, "witchblade red sonja");
        var irrelevantResults = await service.SearchAsync(dir, "abstract geometry shapes");

        // Assert
        Assert.NotEmpty(relevantResults);
        Assert.NotEmpty(irrelevantResults);

        // The relevant query should produce a higher (or equal) score than an irrelevant query
        var relevantScore = relevantResults[0].SimilarityScore;
        var irrelevantScore = irrelevantResults[0].SimilarityScore;

        Assert.True(relevantScore >= irrelevantScore,
            $"Expected relevant query score ({relevantScore}) >= irrelevant query score ({irrelevantScore})");
    }

    [Fact(Skip = "Integration test — requires real CLIP ONNX model, BPE vocabulary, and .cbr file")]
    public async Task SearchAsync_ArchieBeachQuery_BeachIssuesOutrankNonBeach()
    {
        // Arrange
        var dir = @"i:\DVD_Комикс_Арчи\Archie's Girls Betty and Veronica 1-347 + Annuals 1-8 (1950-1987)";

        Assert.True(Directory.Exists(dir), $"Directory not found: {dir}");

        var service = new ComicCoverSearchService(
            _archiveReader, _clipImageEncoder, _clipTextEncoder, _vectorStore, _logger);

        // Act
        var results = await service.SearchAsync(dir, "beach");

        // Assert
        Assert.NotNull(results);
        Assert.NotEmpty(results);

        // Verify known beach-related files are in results with meaningful scores
        var file344 = results.FirstOrDefault(r =>
            r.ArchiveFileName == "Archie's Girls betty and veronica 344 c2c (trango).cbr");
        Assert.NotNull(file344);
        // Beach cover should score in CLIP matched-pair range (0.27–0.37 typical)
        Assert.True(file344.SimilarityScore >= 0.20f,
            $"Expected >= 0.20 for beach issue 344, got {file344.SimilarityScore}");

        var file338 = results.FirstOrDefault(r =>
            r.ArchiveFileName == "Archie's Girls Betty and Veronica 338 c2c (trango).cbr");
        Assert.NotNull(file338);
        Assert.True(file338.SimilarityScore >= 0.20f,
            $"Expected >= 0.20 for beach issue 338, got {file338.SimilarityScore}");

        // Not all covers should pass a 0.25 threshold (with fixed tokenizer)
        var aboveQuarter = results.Count(r => r.SimilarityScore >= 0.25f);
        Assert.True(aboveQuarter < results.Count,
            $"All {results.Count} covers scored >= 0.25 — tokenizer likely still broken");

        // Beach issues should rank in top half (not buried by noise)
        var beachRank344 = results.IndexOf(file344);
        var beachRank338 = results.IndexOf(file338);
        Assert.True(beachRank344 < results.Count / 2,
            $"Beach 344 ranked {beachRank344 + 1}/{results.Count} — too low");
        Assert.True(beachRank338 < results.Count / 2,
            $"Beach 338 ranked {beachRank338 + 1}/{results.Count} — too low");

        // Verify score spread: top score should be at least 0.05 above bottom
        var topScore = results[0].SimilarityScore;
        var bottomScore = results[^1].SimilarityScore;
        Assert.True(topScore - bottomScore >= 0.05f,
            $"Score spread too narrow: top={topScore:F4}, bottom={bottomScore:F4}");

        // Verify cover images exist and have content
        foreach (var result in results)
        {
            Assert.True(File.Exists(result.CoverImagePath),
                $"Cover does not exist: {result.CoverImagePath}");
            Assert.True(new FileInfo(result.CoverImagePath).Length > 0,
                $"Cover is empty: {result.CoverImagePath}");
        }
    }

    public void Dispose()
    {
        _archiveReader.Dispose();
        _clipImageEncoder.Dispose();
        _clipTextEncoder.Dispose();
        _vectorStore.Dispose();
    }
}