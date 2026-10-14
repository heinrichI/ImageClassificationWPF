using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
using ImageClassification.Tests;
using ImageClassification.UI.Configuration;
using ImageClassification.UI.Services;
using ImageClassification.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ImageClassification.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="SearchImageViewModel"/> (Search Image tab). The search service is
/// a fake that returns deterministic results pointing at real 1x1 PNG files, so the
/// thumbnail pipeline's pre-fetch decodes them without errors.
/// </summary>
public class SearchImageViewModelTests : IDisposable
{
    /// <summary>Minimal valid 1x1 RGBA PNG.</summary>
    private static readonly byte[] TinyPng =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    };

    private readonly string _tempDir;
    private readonly string _imgA;
    private readonly string _imgB;
    private readonly FakeImageSearchService _searchService = new();
    private readonly FakeUserSettingsStore _settings = new() { MaxCachedThumbnails = 10 };
    private readonly SearchImageViewModel _vm;

    public SearchImageViewModelTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("image-search-vm-").FullName;
        _imgA = Path.Combine(_tempDir, "a.png");
        _imgB = Path.Combine(_tempDir, "b.png");
        File.WriteAllBytes(_imgA, TinyPng);
        File.WriteAllBytes(_imgB, TinyPng);

        var copyService = new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance);
        var thumbnailProvider = new ThumbnailProvider(
            NullLogger<ThumbnailProvider>.Instance,
            new FakeUserSettingsStore { ThumbnailWidth = 16, ThumbnailHeight = 16 },
            Mock.Of<IComicCoverSearchService>());

        _vm = new SearchImageViewModel(
            _searchService,
            Mock.Of<IVectorStore>(),
            NullLogger<SearchImageViewModel>.Instance,
            thumbnailProvider,
            copyService,
            _settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static ImageSearchResult Result(string path, float score) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        SimilarityScore = score
    };

    private SearchImageViewModel VmWith(params ImageSearchResult[] results)
    {
        _searchService.Results = results.ToList();
        return _vm;
    }

    [Fact]
    public async Task SearchAsync_NoDirectories_PromptsToAddDirectory()
    {
        _vm.QueryText = "autumn";
        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("Please add a search directory first.", _vm.StatusMessage);
        Assert.Empty(_searchService.SeenCalls);
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_PromptsToEnterQuery()
    {
        _vm.SearchDirectories.Add(_tempDir);
        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.StartsWith("Please enter a search query", _vm.StatusMessage);
        Assert.Empty(_searchService.SeenCalls);
    }

    [Fact]
    public async Task SearchAsync_PopulatesResults_PersistsPhraseAndDirectories_SwitchesToResultsPanel()
    {
        _vm.SearchDirectories.Add(_tempDir);
        _vm.QueryText = "autumn forest";
        VmWith(Result(_imgA, 0.9f), Result(_imgB, 0.4f));

        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(2, _vm.Results.Count);
        Assert.Equal(0.9f, _vm.Results[0].SimilarityScore, 3); // sorted by score
        Assert.Equal(1, _vm.SelectedInnerTabIndex);            // auto-switched to results
        Assert.StartsWith("Found 2 matching images", _vm.StatusMessage);

        // Persistence: directories + phrase history (newest first).
        Assert.Equal(new[] { _tempDir }, _settings.ImageSearchDirectories);
        Assert.Equal("autumn forest", _settings.ImageSearchPhraseHistory[0]);
        Assert.False(_vm.IsBusy);
    }

    [Fact]
    public async Task SearchAsync_FiltersResultsBySimilarityThreshold()
    {
        _vm.SearchDirectories.Add(_tempDir);
        _vm.QueryText = "autumn";
        _vm.SimilarityThreshold = 0.5;
        VmWith(Result(_imgA, 0.9f), Result(_imgB, 0.4f));

        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Single(_vm.Results);
        Assert.Equal(_imgA, _vm.Results[0].FilePath);
        Assert.Equal(0.5, _settings.ImageSearchSimilarityThreshold, 3); // threshold persisted
    }

    [Fact]
    public async Task SearchAsync_AllBelowThreshold_ReportsNoResultsAboveThreshold()
    {
        _vm.SearchDirectories.Add(_tempDir);
        _vm.QueryText = "autumn";
        _vm.SimilarityThreshold = 0.99;
        VmWith(Result(_imgA, 0.9f));

        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Empty(_vm.Results);
        Assert.Equal("No results above the similarity threshold.", _vm.StatusMessage);
        Assert.Equal(0, _vm.SelectedInnerTabIndex); // stays on the search panel
    }

    [Fact]
    public async Task SearchAsync_Cancelled_ReportsCancelled()
    {
        _vm.SearchDirectories.Add(_tempDir);
        _vm.QueryText = "autumn";
        _searchService.Throw = new OperationCanceledException();

        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("Search cancelled.", _vm.StatusMessage);
        Assert.False(_vm.IsBusy);
    }

    [Fact]
    public async Task SearchAsync_PassesRecurseOptionToService()
    {
        _vm.SearchDirectories.Add(_tempDir);
        _vm.QueryText = "autumn";
        _vm.RecurseSubfolders = false;
        VmWith(Result(_imgA, 0.9f));

        await _vm.SearchCommand.ExecuteAsync(null);

        var options = Assert.Single(_searchService.SeenOptions);
        Assert.False(options!.RecurseSubfolders);
    }

    [Fact]
    public async Task SearchAsync_RestoresAndPersistsRecurseSetting()
    {
        _settings.ImageSearchRecurseSubfolders = false;
        var vm = new SearchImageViewModel(
            _searchService,
            Mock.Of<IVectorStore>(),
            NullLogger<SearchImageViewModel>.Instance,
            new ThumbnailProvider(
                NullLogger<ThumbnailProvider>.Instance,
                new FakeUserSettingsStore { ThumbnailWidth = 16 },
                Mock.Of<IComicCoverSearchService>()),
            new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance),
            _settings);

        Assert.False(vm.RecurseSubfolders);

        vm.RecurseSubfolders = true;
        Assert.True(_settings.ImageSearchRecurseSubfolders);
    }

    [Fact]
    public async Task CopyResultsImages_CopiesResultFiles()
    {
        _vm.SearchDirectories.Add(_tempDir);
        _vm.QueryText = "autumn";
        VmWith(Result(_imgA, 0.9f), Result(_imgB, 0.4f));
        await _vm.SearchCommand.ExecuteAsync(null);

        var dest = Directory.CreateTempSubdirectory("image-search-copy-").FullName;
        try
        {
            await _vm.CopyResultsImagesCoreAsync(dest);

            Assert.True(File.Exists(Path.Combine(dest, "a.png")));
            Assert.True(File.Exists(Path.Combine(dest, "b.png")));
            Assert.StartsWith("Copied 2 of 2 images", _vm.StatusMessage);
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public void RequestThumbnail_EnqueuesItemThroughProvider()
    {
        // The provider with a 16px width and real files: the request is accepted (no throw)
        // and the item is a SearchImageItem the behavior passes as its data context.
        var item = new ImageClassification.UI.Models.SearchImageItem { FilePath = _imgA };
        _vm.RequestThumbnail(item);
        _vm.RequestThumbnail(null); // must be a no-op
    }

    [Fact]
    public void AddDirectories_AddsValidDirectories_SkipsMissingAndDuplicates_Persists()
    {
        var newDir = System.IO.Directory.CreateTempSubdirectory("add-dirs-").FullName;
        try
        {
            // Act: the payload a folder drag from Explorer would produce.
            _vm.AddDirectoriesCommand.Execute(new[]
            {
                newDir,                       // valid → added
                Path.Combine(_tempDir, "no-such-subdir"), // missing → skipped
                _tempDir,                     // valid → added
                _tempDir.ToUpperInvariant(),  // case-insensitive duplicate → skipped
            });

            // Assert
            Assert.Equal(new[] { newDir, _tempDir }, _vm.SearchDirectories.ToArray());
            // The list was persisted to the settings store exactly once with the same content.
            Assert.Equal(_vm.SearchDirectories.ToList(), _settings.ImageSearchDirectories.ToList());
        }
        finally
        {
            try { System.IO.Directory.Delete(newDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Fake <see cref="IImageSearchService"/>: returns <see cref="Results"/> (or throws
    /// <see cref="Throw"/>) and records the calls/options it saw.
    /// </summary>
    private sealed class FakeImageSearchService : IImageSearchService
    {
        public List<ImageSearchResult> Results { get; set; } = new();
        public Exception? Throw { get; set; }
        public List<(string Directory, string Query)> SeenCalls { get; } = new();
        public List<ImageSearchOptions?> SeenOptions { get; } = new();

        public Task<List<ImageSearchResult>> SearchAsync(
            string directoryPath,
            string query,
            ImageSearchOptions? options = null,
            IProgress<ImageSearchStatus>? status = null,
            CancellationToken ct = default)
        {
            SeenCalls.Add((directoryPath, query));
            SeenOptions.Add(options);
            if (Throw is not null)
                throw Throw;
            return Task.FromResult(Results);
        }
    }
}