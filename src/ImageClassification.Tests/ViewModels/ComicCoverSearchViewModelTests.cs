using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
using ImageClassification.UI.Configuration;
using ImageClassification.UI.Services;
using ImageClassification.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using System.Linq;
using System.Windows.Media.Imaging;
using Moq;

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

        public Task<byte[]?> ExtractCoverAsync(string archivePath) => Task.FromResult<byte[]?>(null);

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
            new ComicCoverResult { ArchivePath = "a1", ArchiveFileName = "a1.cbr", SimilarityScore = 0.10f },
            new ComicCoverResult { ArchivePath = "a2", ArchiveFileName = "a2.cbr", SimilarityScore = 0.25f },
            new ComicCoverResult { ArchivePath = "a3", ArchiveFileName = "a3.cbr", SimilarityScore = 0.30f }
        };

        var fakeService = new FakeSearchService(results);
        var thumbnailSettings = new ImageClassification.UI.Configuration.ThumbnailSettings { ThumbnailWidth = 64, ThumbnailHeight = 96 };
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance, thumbnailSettings, fakeService);
        var copyService = new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance);

        var vm = new ComicCoverSearchViewModel(fakeService, NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService)
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

    [Fact]
    public async Task CopyResultsImagesCoreAsync_CopiesAllResultImages()
    {
        // Arrange
        var archiveReader = new Mock<IArchiveReader>();
        byte[] pngBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
        archiveReader.Setup(r => r.ExtractImageToMemoryAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(pngBytes);
        var copyService = new ImageCopyService(archiveReader.Object, NullLogger<ImageCopyService>.Instance);

        var fakeService = new FakeSearchService(new List<ComicCoverResult>());
        var thumbnailSettings = new ImageClassification.UI.Configuration.ThumbnailSettings { ThumbnailWidth = 64, ThumbnailHeight = 96 };
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(
            NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance, thumbnailSettings, fakeService);
        var vm = new ComicCoverSearchViewModel(fakeService, NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService);

        vm.Results.Add(new ComicCoverItem { ArchivePath = "a.cbz", ArchiveFileName = "a.cbz", PageIndex = 2 });
        vm.Results.Add(new ComicCoverItem { ArchivePath = "b.cbr", ArchiveFileName = "b.cbr", PageIndex = 0 });
        Assert.True(vm.CopyResultsImagesCommand.CanExecute(null));

        var dest = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "imgcopy_vm_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dest);
        try
        {
            // Act
            await vm.CopyResultsImagesCoreAsync(dest);

            // Assert
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(dest, "a_p3.png")));
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(dest, "b_p1.png")));
            Assert.Contains("Copied 2 of 2", vm.StatusMessage);
        }
        finally
        {
            System.IO.Directory.Delete(dest, true);
        }
    }

    [Fact]
    public void CopyResultsImagesCommand_Disabled_WhenNoResults()
    {
        var fakeService = new FakeSearchService(new List<ComicCoverResult>());
        var thumbnailSettings = new ImageClassification.UI.Configuration.ThumbnailSettings { ThumbnailWidth = 64, ThumbnailHeight = 96 };
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(
            NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance, thumbnailSettings, fakeService);
        var copyService = new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance);
        var vm = new ComicCoverSearchViewModel(fakeService, NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService);

        Assert.False(vm.CopyResultsImagesCommand.CanExecute(null));

        vm.Results.Add(new ComicCoverItem { ArchivePath = "a.cbz", ArchiveFileName = "a.cbz", PageIndex = 0 });
        Assert.True(vm.CopyResultsImagesCommand.CanExecute(null));
    }

    [Fact]
    public async Task CopyResultsImagesCommand_Enabled_AfterSearchCompletes()
    {
        // Arrange: results that pass the threshold
        var results = new List<ComicCoverResult>
        {
            new ComicCoverResult { ArchivePath = "a1", ArchiveFileName = "a1.cbr", SimilarityScore = 0.60f },
            new ComicCoverResult { ArchivePath = "a2", ArchiveFileName = "a2.cbr", SimilarityScore = 0.70f }
        };
        var fakeService = new FakeSearchService(results);
        var thumbnailSettings = new ImageClassification.UI.Configuration.ThumbnailSettings { ThumbnailWidth = 64, ThumbnailHeight = 96 };
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(
            NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance, thumbnailSettings, fakeService);
        var copyService = new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance);
        var vm = new ComicCoverSearchViewModel(fakeService, NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService)
        {
            DirectoryPath = "any",
            QueryText = "q",
            SimilarityThreshold = 0.25
        };

        // Track the CanExecute value at the moment of every notification
        bool? lastCanExecute = null;
        vm.CopyResultsImagesCommand.CanExecuteChanged += (_, _) =>
            lastCanExecute = vm.CopyResultsImagesCommand.CanExecute(null);

        // Act
        var mi = typeof(ComicCoverSearchViewModel).GetMethod("SearchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)mi.Invoke(vm, null)!;

        // Assert: the final notification must evaluate to true (IsBusy already false)
        Assert.True(lastCanExecute);
        Assert.True(vm.CopyResultsImagesCommand.CanExecute(null));
    }
}