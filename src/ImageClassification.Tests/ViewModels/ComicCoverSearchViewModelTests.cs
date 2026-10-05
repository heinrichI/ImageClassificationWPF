using System.Collections.Generic;
using ImageClassification.Tests;
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

        public Task<List<ComicCoverResult>> SearchAsync(string directoryPath, string query, IProgress<ComicSearchStatus>? status = null, CancellationToken ct = default)
        {
            status?.Report(new ComicSearchStatus { Phase = "Extracting & encoding covers (GPU)..." });
            return Task.FromResult(_results.ToList());
        }

        public Task<byte[]?> ExtractPageAsync(string archivePath, int pageIndex) => Task.FromResult<byte[]?>(null);

        public Task<List<ComicCoverResult>> SearchAllPagesAsync(string directoryPath, string query, IProgress<ComicSearchStatus>? status = null, CancellationToken ct = default)
        {
            status?.Report(new ComicSearchStatus { Phase = "Extracting & encoding pages (GPU)..." });
            return Task.FromResult(_results.ToList());
        }

        public Task ClearCacheAsync() => Task.CompletedTask;

        public void Dispose() { }
    }

    /// <summary>
    /// Reports one status phase and one progress tick, then waits until the test
    /// completes the gate — used to observe the in-flight StatusMessage.
    /// </summary>
    private sealed class GatedSearchService : IComicCoverSearchService
    {
        private readonly List<ComicCoverResult> _results;

        public TaskCompletionSource<bool> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedSearchService(List<ComicCoverResult> results) => _results = results;

        public Task LoadModelAsync(string clipOnnxPath) => Task.CompletedTask;

        public Task<byte[]?> ExtractPageAsync(string archivePath, int pageIndex) => Task.FromResult<byte[]?>(null);

        public Task ClearCacheAsync() => Task.CompletedTask;

        public void Dispose() { }

        public Task<List<ComicCoverResult>> SearchAsync(string directoryPath, string query,
            IProgress<ComicSearchStatus>? status = null, CancellationToken ct = default)
        {
            status?.Report(new ComicSearchStatus { Phase = "Extracting & encoding covers (GPU)..." });
            status?.Report(new ComicSearchStatus { Current = 1, Total = 5 });

            return Gate.Task.ContinueWith(
                _ => _results.ToList(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public Task<List<ComicCoverResult>> SearchAllPagesAsync(string directoryPath, string query,
            IProgress<ComicSearchStatus>? status = null, CancellationToken ct = default)
            => SearchAsync(directoryPath, query, status, ct);
    }

    /// <summary>
    /// Reports one FULL detail status update (Detail set) and one progress tick,
    /// then waits on the gate — used to verify the detail line is shown verbatim,
    /// without the progress counter appended.
    /// </summary>
    private sealed class DetailGatedSearchService : IComicCoverSearchService
    {
        public const string DetailLine = "Processing archives: 1/2 · GPU 1 batches · queue 0";

        public TaskCompletionSource<bool> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task LoadModelAsync(string clipOnnxPath) => Task.CompletedTask;

        public Task<byte[]?> ExtractPageAsync(string archivePath, int pageIndex) => Task.FromResult<byte[]?>(null);

        public Task ClearCacheAsync() => Task.CompletedTask;

        public void Dispose() { }

        public Task<List<ComicCoverResult>> SearchAsync(string directoryPath, string query,
            IProgress<ComicSearchStatus>? status = null, CancellationToken ct = default)
        {
            status?.Report(new ComicSearchStatus { Detail = DetailLine, Current = 1, Total = 2 });

            return Gate.Task.ContinueWith(
                _ => new List<ComicCoverResult>
                {
                    new ComicCoverResult { ArchivePath = "a1", ArchiveFileName = "a1.cbr", SimilarityScore = 0.9f }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public Task<List<ComicCoverResult>> SearchAllPagesAsync(string directoryPath, string query,
            IProgress<ComicSearchStatus>? status = null, CancellationToken ct = default)
            => SearchAsync(directoryPath, query, status, ct);
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
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance, new FakeUserSettingsStore { ThumbnailWidth = 64, ThumbnailHeight = 96 }, fakeService);
        var copyService = new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance);

        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService)
        {
            DirectoryPath = System.IO.Directory.GetCurrentDirectory(),
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
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(
            NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance, new FakeUserSettingsStore { ThumbnailWidth = 64, ThumbnailHeight = 96 }, fakeService);
        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService);

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
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(
            NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance, new FakeUserSettingsStore { ThumbnailWidth = 64, ThumbnailHeight = 96 }, fakeService);
        var copyService = new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance);
        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService);

        Assert.False(vm.CopyResultsImagesCommand.CanExecute(null));

        vm.Results.Add(new ComicCoverItem { ArchivePath = "a.cbz", ArchiveFileName = "a.cbz", PageIndex = 0 });
        Assert.True(vm.CopyResultsImagesCommand.CanExecute(null));
    }

    [Fact]
    public async Task SearchAsync_PhaseShownInStatusMessage_WhileSearching()
    {
        // Arrange: service reports a phase + (1/5) progress, then blocks on the gate
        var results = new List<ComicCoverResult>
        {
            new ComicCoverResult { ArchivePath = "a1", ArchiveFileName = "a1.cbr", SimilarityScore = 0.9f }
        };
        var fakeService = new GatedSearchService(results);
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(
            NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance, new FakeUserSettingsStore { ThumbnailWidth = 64, ThumbnailHeight = 96 }, fakeService);
        var copyService = new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance);

        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService)
        {
            DirectoryPath = System.IO.Directory.GetCurrentDirectory(),
            QueryText = "q",
            SimilarityThreshold = 0.25
        };

        var mi = typeof(ComicCoverSearchViewModel).GetMethod("SearchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var searchTask = (Task)mi.Invoke(vm, null)!;

        // Wait until the VM reflected both the phase and the counter in StatusMessage
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!vm.StatusMessage.Contains("/5", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Contains("Extracting & encoding covers (GPU)", vm.StatusMessage);
        Assert.Contains("1/5", vm.StatusMessage);

        // Release the gate and verify the final message replaces the phase
        fakeService.Gate.SetResult(true);
        await searchTask;

        Assert.StartsWith("Found 1 matching covers", vm.StatusMessage);
    }

    [Fact]
    public async Task SearchAsync_FullDetailStatusLine_ShownAsIs()
    {
        // Arrange: service reports a full detail line (no trailing "...") + (1/2) progress,
        // then blocks on the gate
        var fakeService = new DetailGatedSearchService();
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(
            NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance, new FakeUserSettingsStore { ThumbnailWidth = 64, ThumbnailHeight = 96 }, fakeService);
        var copyService = new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance);

        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService)
        {
            DirectoryPath = System.IO.Directory.GetCurrentDirectory(),
            QueryText = "q",
            SimilarityThreshold = 0.25
        };

        var mi = typeof(ComicCoverSearchViewModel).GetMethod("SearchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var searchTask = (Task)mi.Invoke(vm, null)!;

        // Wait until the detail line reached the UI
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!vm.StatusMessage.Contains("queue", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        // The line is shown verbatim — the (1/2) progress counter is NOT appended
        Assert.Equal(DetailGatedSearchService.DetailLine, vm.StatusMessage);

        // Release the gate and verify the final message replaces the detail line
        fakeService.Gate.SetResult(true);
        await searchTask;

        Assert.StartsWith("Found 1 matching covers", vm.StatusMessage);
    }

    [Fact]
    public async Task SearchAsync_NonExistentDirectory_ReportsErrorWithoutSearching()
    {
        // Arrange: fake that would produce results if the search actually ran
        var results = new List<ComicCoverResult>
        {
            new ComicCoverResult { ArchivePath = "a1", ArchiveFileName = "a1.cbr", SimilarityScore = 0.9f }
        };
        var fakeService = new FakeSearchService(results);
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(
            NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance, new FakeUserSettingsStore { ThumbnailWidth = 64, ThumbnailHeight = 96 }, fakeService);
        var copyService = new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance);
        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService)
        {
            DirectoryPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "definitely_missing_dir_" + Guid.NewGuid().ToString("N")),
            QueryText = "q"
        };

        // Act
        var mi = typeof(ComicCoverSearchViewModel).GetMethod("SearchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)mi.Invoke(vm, null)!;

        // Assert: search was short-circuited before the service call
        Assert.Empty(vm.Results);
        Assert.Contains("Directory not found", vm.StatusMessage);
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
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(
            NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance, new FakeUserSettingsStore { ThumbnailWidth = 64, ThumbnailHeight = 96 }, fakeService);
        var copyService = new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance);
        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService)
        {
            DirectoryPath = System.IO.Directory.GetCurrentDirectory(),
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

    /// <summary>
    /// Reports two archive-note diagnostics (a container mismatch and a bulk fallback)
    /// on the status channel, then waits on the gate so the test thread can pump the
    /// status handlers while the search is still in flight (handlers delivered after
    /// the search finishes are stale and intentionally dropped by the VM).
    /// A fresh gate is created for every search invocation.
    /// </summary>
    private sealed class NotesSearchService : IComicCoverSearchService
    {
        public const string NoteMismatch = "a.cbr: ZIP container inside '.cbr' — re-extracted with ZIP handler";
        public const string NoteFallback = "b.cbr: bulk failed (7z Open failed) — per-entry fallback";

        /// <summary>Gate for the current (in-flight) search; re-created on each call.</summary>
        public TaskCompletionSource<bool> Gate { get; private set; } = new();

        public Task LoadModelAsync(string clipOnnxPath) => Task.CompletedTask;

        public Task<byte[]?> ExtractPageAsync(string archivePath, int pageIndex) => Task.FromResult<byte[]?>(null);

        public Task ClearCacheAsync() => Task.CompletedTask;

        public void Dispose() { }

        public async Task<List<ComicCoverResult>> SearchAsync(string directoryPath, string query,
            IProgress<ComicSearchStatus>? status = null, CancellationToken ct = default)
        {
            Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            status?.Report(new ComicSearchStatus { Note = NoteMismatch });
            status?.Report(new ComicSearchStatus { Note = NoteFallback });
            await Gate.Task.ConfigureAwait(false);
            return new List<ComicCoverResult>
            {
                new ComicCoverResult { ArchivePath = "a1", ArchiveFileName = "a.cbr", SimilarityScore = 0.9f }
            };
        }

        public Task<List<ComicCoverResult>> SearchAllPagesAsync(string directoryPath, string query,
            IProgress<ComicSearchStatus>? status = null, CancellationToken ct = default)
            => SearchAsync(directoryPath, query, status, ct);
    }

    [Fact]
    public async Task SearchAsync_ArchiveNotes_Accumulate_AndAreClearedOnNewSearch()
    {
        // Arrange: service reports two notes per search, then holds the search in flight
        var fakeService = new NotesSearchService();
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(
            NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance, new FakeUserSettingsStore { ThumbnailWidth = 64, ThumbnailHeight = 96 }, fakeService);
        var copyService = new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance);
        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService)
        {
            DirectoryPath = System.IO.Directory.GetCurrentDirectory(),
            QueryText = "q",
            SimilarityThreshold = 0.25
        };

        var mi = typeof(ComicCoverSearchViewModel).GetMethod("SearchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        // Act: first search (in flight, held by the gate)
        var searchTask = (Task)mi.Invoke(vm, null)!;

        // Wait until the in-flight search's status handlers applied both notes
        // (the test thread pumps the posted status updates while awaiting)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.ArchiveNotes.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        // Assert: both notes accumulated; visibility counter matches
        // (order is not asserted: the test SynchronizationContext may deliver
        // posted status updates out of order — the WPF dispatcher is FIFO)
        Assert.Equal(2, vm.ArchiveNotes.Count);
        Assert.Contains(NotesSearchService.NoteMismatch, vm.ArchiveNotes);
        Assert.Contains(NotesSearchService.NoteFallback, vm.ArchiveNotes);
        Assert.Equal(vm.ArchiveNotes.Count, vm.ArchiveNotesCount);

        // Complete the first search
        fakeService.Gate.SetResult(true);
        await searchTask;

        // Act: a new search must start from an empty list (in flight again)
        searchTask = (Task)mi.Invoke(vm, null)!;
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.ArchiveNotes.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        // Assert: the notes were cleared before re-reporting (2, not 4)
        Assert.Equal(2, vm.ArchiveNotes.Count);
        Assert.Contains(NotesSearchService.NoteMismatch, vm.ArchiveNotes);
        Assert.Contains(NotesSearchService.NoteFallback, vm.ArchiveNotes);

        fakeService.Gate.SetResult(true);
        await searchTask;
    }
}