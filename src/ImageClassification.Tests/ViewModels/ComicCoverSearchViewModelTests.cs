using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
using ImageClassification.UI.Configuration;
using ImageClassification.UI.Services;
using ImageClassification.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using System.Linq;
using System.Windows.Media.Imaging;

namespace ImageClassification.Tests.ViewModels;

public class ComicCoverSearchViewModelTests
{
    private class FakeSearchService : IComicCoverSearchService
    {
        private readonly List<ComicCoverResult> _results;

        public FakeSearchService(List<ComicCoverResult> results)
        {
            _results = results;
        }

        public Task LoadModelAsync(string clipOnnxPath) => Task.CompletedTask;

        public Task<List<ComicCoverResult>> SearchAsync(string directoryPath, string query, IProgress<(int Current, int Total)>? progress = null, CancellationToken ct = default)
        {
            return Task.FromResult(_results.ToList());
        }

        public Task<string?> ExtractCoverAsync(string archivePath) => Task.FromResult<string?>(null);

        public Task<List<ComicCoverResult>> SearchAllPagesAsync(string directoryPath, string query, IProgress<(int Current, int Total)>? progress = null, CancellationToken ct = default)
        {
            return Task.FromResult(_results.ToList());
        }

        public Task ClearCacheAsync() => Task.CompletedTask;

        public void Dispose() { }
    }

    [Fact]
    public async Task SearchAsync_FiltersBySimilarityThreshold_Double()
    {
        // Arrange
        var results = new List<ComicCoverResult>
        {
            new ComicCoverResult { ArchivePath = "a1", ArchiveFileName = "a1.cbr", CoverImagePath = "cover1.png", SimilarityScore = 0.10f },
            new ComicCoverResult { ArchivePath = "a2", ArchiveFileName = "a2.cbr", CoverImagePath = "cover2.png", SimilarityScore = 0.25f },
            new ComicCoverResult { ArchivePath = "a3", ArchiveFileName = "a3.cbr", CoverImagePath = "cover3.png", SimilarityScore = 0.30f }
        };

        var fakeService = new FakeSearchService(results);
        var thumbnailSettings = new ImageClassification.UI.Configuration.ThumbnailSettings { ThumbnailWidth = 64, ThumbnailHeight = 96 };
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance, thumbnailSettings, fakeService);

        var vm = new ComicCoverSearchViewModel(fakeService, NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider)
        {
            DirectoryPath = "any",
            QueryText = "q",
            SimilarityThreshold = 0.25 // double
        };

        // Act - invoke the generated command's underlying method via reflection (SearchAsync is private)
        var mi = typeof(ComicCoverSearchViewModel).GetMethod("SearchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)mi.Invoke(vm, null)!;

        // Assert
        Assert.NotNull(vm.Results);
        var found = vm.Results.ToList();
        Assert.Equal(2, found.Count);
        Assert.All(found, item => Assert.True(item.SimilarityScore >= 0.25f));
    }
}