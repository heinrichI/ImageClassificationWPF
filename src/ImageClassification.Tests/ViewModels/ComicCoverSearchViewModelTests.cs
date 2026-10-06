using System.Collections.Generic;
using System.Linq;
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
        private readonly Dictionary<string, List<ComicCoverResult>> _byDirectory = new(StringComparer.OrdinalIgnoreCase);
        private int _searchCalls;

        public FakeSearchService(List<ComicCoverResult> results)
        {
            _results = results;
        }

        /// <summary>Overrides the default results for one specific root directory.</summary>
        public void SetResultsForDirectory(string directory, List<ComicCoverResult> results)
        {
            _byDirectory[directory] = results;
        }

        /// <summary>Number of search invocations (one per root directory, thread-safe).</summary>
        public int SearchCalls => Interlocked.CompareExchange(ref _searchCalls, 0, 0);

        public Task LoadModelAsync(string clipOnnxPath) => Task.CompletedTask;

        public Task<List<ComicCoverResult>> SearchAsync(string directoryPath, string query, IProgress<ComicSearchStatus>? status = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _searchCalls);
            status?.Report(new ComicSearchStatus { Phase = "Extracting & encoding covers (GPU)..." });
            var results = _byDirectory.TryGetValue(directoryPath, out var r) ? r : _results;
            return Task.FromResult(results.ToList());
        }

        public Task<byte[]?> ExtractPageAsync(string archivePath, int pageIndex)
        {
            Interlocked.Increment(ref _extractCalls);
            return Task.FromResult<byte[]?>(null);
        }

        /// <summary>Number of preview extractions requested (thread-safe).</summary>
        public int ExtractCalls => Interlocked.CompareExchange(ref _extractCalls, 0, 0);

        private int _extractCalls;

        public Task<List<ComicCoverResult>> SearchAllPagesAsync(string directoryPath, string query, IProgress<ComicSearchStatus>? status = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _searchCalls);
            status?.Report(new ComicSearchStatus { Phase = "Extracting & encoding pages (GPU)..." });
            var results = _byDirectory.TryGetValue(directoryPath, out var r) ? r : _results;
            return Task.FromResult(results.ToList());
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

        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService, new FakeUserSettingsStore())
        {
            SearchDirectories = { System.IO.Directory.GetCurrentDirectory() },
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
        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService, new FakeUserSettingsStore());

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
        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService, new FakeUserSettingsStore());

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

        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService, new FakeUserSettingsStore())
        {
            SearchDirectories = { System.IO.Directory.GetCurrentDirectory() },
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

        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService, new FakeUserSettingsStore())
        {
            SearchDirectories = { System.IO.Directory.GetCurrentDirectory() },
            QueryText = "q",
            SimilarityThreshold = 0.25
        };

        var mi = typeof(ComicCoverSearchViewModel).GetMethod("SearchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var searchTask = (Task)mi.Invoke(vm, null)!;

        // Wait until the detail line reached the UI
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!vm.StatusMessage.Contains("queue", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        // The line is shown verbatim (with the per-root directory prefix the VM adds) —
        // the (1/2) progress counter is NOT appended
        Assert.Equal($"{System.IO.Directory.GetCurrentDirectory()} | {DetailGatedSearchService.DetailLine}", vm.StatusMessage);

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
        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService, new FakeUserSettingsStore())
        {
            SearchDirectories = { System.IO.Path.Combine(System.IO.Path.GetTempPath(), "definitely_missing_dir_" + Guid.NewGuid().ToString("N")) },
            QueryText = "q"
        };

        // Act
        var mi = typeof(ComicCoverSearchViewModel).GetMethod("SearchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)mi.Invoke(vm, null)!;

        // Assert: the missing root is skipped (no service call) and recorded in the notes
        Assert.Empty(vm.Results);
        Assert.Equal(0, fakeService.SearchCalls);
        Assert.Contains(vm.ArchiveNotes, n => n.Contains("Directory not found"));
        Assert.Contains("see notes", vm.StatusMessage);
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
        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService, new FakeUserSettingsStore())
        {
            SearchDirectories = { System.IO.Directory.GetCurrentDirectory() },
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

    private sealed class CurrentFileSearchService : IComicCoverSearchService
    {
        public const string File = @"I:\Comics\test.cbr";

        public TaskCompletionSource<bool> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task LoadModelAsync(string clipOnnxPath) => Task.CompletedTask;

        public Task<byte[]?> ExtractPageAsync(string archivePath, int pageIndex) => Task.FromResult<byte[]?>(null);

        public Task ClearCacheAsync() => Task.CompletedTask;

        public void Dispose() { }

        public async Task<List<ComicCoverResult>> SearchAsync(string directoryPath, string query,
            IProgress<ComicSearchStatus>? status = null, CancellationToken ct = default)
        {
            status?.Report(new ComicSearchStatus { CurrentFile = File });
            await Gate.Task.ConfigureAwait(false);
            return [];
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
        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService, new FakeUserSettingsStore())
        {
            SearchDirectories = { System.IO.Directory.GetCurrentDirectory() },
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

    [Fact]
    public async Task SearchAsync_CurrentFileShownDuringSearch_AndClearedAfter()
    {
        var fakeService = new CurrentFileSearchService();
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(
            NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance, new FakeUserSettingsStore { ThumbnailWidth = 64, ThumbnailHeight = 96 }, fakeService);
        var copyService = new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance);
        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService, new FakeUserSettingsStore())
        {
            SearchDirectories = { System.IO.Directory.GetCurrentDirectory() },
            QueryText = "q"
        };

        var mi = typeof(ComicCoverSearchViewModel).GetMethod("SearchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        // Act: search in flight (held by the gate after reporting the current file)
        var searchTask = (Task)mi.Invoke(vm, null)!;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.CurrentFile != CurrentFileSearchService.File && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        // Assert: the label property carries the reported path while the search runs
        Assert.Equal(CurrentFileSearchService.File, vm.CurrentFile);

        // Act: let the search finish
        fakeService.Gate.SetResult(true);
        await searchTask;

        // Assert: back to idle — the label is cleared again
        Assert.Equal(string.Empty, vm.CurrentFile);
    }

    /// <summary>
    /// The search pre-queues only the first results, up to the thumbnail cache limit — the
    /// rest are loaded on demand the moment their tile is scrolled into view. Pre-fetching
    /// beyond the limit would be wasted archive IO (the tiles would be evicted from the LRU
    /// cache before they become visible).
    /// </summary>
    [Fact]
    public async Task SearchAsync_PrefetchesOnlyUpToCacheLimit()
    {
        // Arrange: five results above the threshold
        var results = new List<ComicCoverResult>
        {
            new ComicCoverResult { ArchivePath = "p1", ArchiveFileName = "1.cbr", SimilarityScore = 0.95f },
            new ComicCoverResult { ArchivePath = "p2", ArchiveFileName = "2.cbr", SimilarityScore = 0.90f },
            new ComicCoverResult { ArchivePath = "p3", ArchiveFileName = "3.cbr", SimilarityScore = 0.85f },
            new ComicCoverResult { ArchivePath = "p4", ArchiveFileName = "4.cbr", SimilarityScore = 0.80f },
            new ComicCoverResult { ArchivePath = "p5", ArchiveFileName = "5.cbr", SimilarityScore = 0.75f }
        };
        var fakeService = new FakeSearchService(results);
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(
            NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance,
            new FakeUserSettingsStore { ThumbnailWidth = 64, ThumbnailHeight = 96 }, fakeService);
        var copyService = new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance);
        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService,
            new FakeUserSettingsStore { MaxCachedThumbnails = 3 })
        {
            SearchDirectories = { System.IO.Directory.GetCurrentDirectory() },
            QueryText = "q",
            SimilarityThreshold = 0.25
        };

        // Act
        var mi = typeof(ComicCoverSearchViewModel).GetMethod("SearchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)mi.Invoke(vm, null)!;

        // Assert: all five results are listed, in service (score) order
        Assert.Equal(5, vm.Results.Count);
        Assert.Equal("1.cbr", vm.Results[0].ArchiveFileName);
        Assert.Equal("5.cbr", vm.Results[4].ArchiveFileName);
        Assert.DoesNotContain("showing first", vm.StatusMessage);

        // Only the first 3 (the cache limit) are pre-queued; the rest wait for visibility.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (fakeService.ExtractCalls < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Task.Delay(100); // grace: make sure nothing beyond the limit sneaks in
        Assert.Equal(3, fakeService.ExtractCalls);
    }

    /// <summary>
    /// Re-requesting a tile that is already in the pipeline (its visibility event fires
    /// again) is a priority bump, not a new extraction — no duplicate archive IO.
    /// </summary>
    [Fact]
    public async Task RequestThumbnail_BumpDoesNotReExtract()
    {
        var results = new List<ComicCoverResult>
        {
            new ComicCoverResult { ArchivePath = "p1", ArchiveFileName = "1.cbr", SimilarityScore = 0.95f },
            new ComicCoverResult { ArchivePath = "p2", ArchiveFileName = "2.cbr", SimilarityScore = 0.90f }
        };
        var fakeService = new FakeSearchService(results);
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(
            NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance,
            new FakeUserSettingsStore { ThumbnailWidth = 64, ThumbnailHeight = 96 }, fakeService);
        var copyService = new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance);
        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(), NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService,
            new FakeUserSettingsStore())
        {
            SearchDirectories = { System.IO.Directory.GetCurrentDirectory() },
            QueryText = "q",
            SimilarityThreshold = 0.25
        };

        var mi = typeof(ComicCoverSearchViewModel).GetMethod("SearchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)mi.Invoke(vm, null)!;
        Assert.Equal(2, vm.Results.Count);

        // Both results are pre-queued by the search.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (fakeService.ExtractCalls < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(2, fakeService.ExtractCalls);

        // Act: tile 0 keeps reporting visibility (scrolling back and forth).
        vm.RequestThumbnail(vm.Results[0]);
        vm.RequestThumbnail(vm.Results[0]);
        vm.RequestThumbnail(vm.Results[0]);

        // Assert: bumps only re-order — no additional extraction.
        await Task.Delay(100);
        Assert.Equal(2, fakeService.ExtractCalls);
    }

    [Fact]
    public void Constructor_RestoresSearchModeFromSettings()
    {
        var settings = new FakeUserSettingsStore { SearchMode = 1 };
        var vm = CreateModeVm(settings);

        Assert.Equal(1, vm.SelectedModeIndex); // All pages restored
        Assert.Equal(1, settings.SearchMode); // restoring must not rewrite the store
    }

    [Fact]
    public void ChangingSearchMode_PersistsToSettings()
    {
        var settings = new FakeUserSettingsStore { SearchMode = 0 };
        var vm = CreateModeVm(settings);

        vm.SelectedModeIndex = 1; // covers -> all pages

        Assert.Equal(1, settings.SearchMode);
    }

    [Fact]
    public void RevertingSearchMode_DoesNotRewriteSettings()
    {
        var settings = new FakeUserSettingsStore { SearchMode = 1 };
        var vm = CreateModeVm(settings);
        vm.SelectedModeIndex = 1; // same value as stored — no save

        Assert.Equal(1, settings.SearchMode);
    }

    [Fact]
    public void Constructor_RestoresSimilarityThresholdFromSettings()
    {
        var settings = new FakeUserSettingsStore { SimilarityThreshold = 0.42 };
        var vm = CreateModeVm(settings);

        Assert.Equal(0.42, vm.SimilarityThreshold, 9);
    }

    [Fact]
    public void ChangingSimilarityThreshold_PersistsToSettings()
    {
        var settings = new FakeUserSettingsStore();
        var vm = CreateModeVm(settings);

        vm.SimilarityThreshold = 0.6;

        Assert.Equal(0.6, settings.SimilarityThreshold, 9);
    }

    [Fact]
    public void RevertingSimilarityThreshold_DoesNotRewriteSettings()
    {
        var settings = new FakeUserSettingsStore { SimilarityThreshold = 0.42 };
        var vm = CreateModeVm(settings); // constructor restore must not save (guard)
        vm.SimilarityThreshold = 0.42; // same value as stored — no save

        Assert.Equal(0.42, settings.SimilarityThreshold);
    }

    [Fact]
    public async Task Search_MergesResultsFromAllDirectories_AndDeduplicatesByArchiveAndPage()
    {
        var dirA = System.IO.Directory.CreateTempSubdirectory("mergeA").FullName;
        var dirB = System.IO.Directory.CreateTempSubdirectory("mergeB").FullName;
        var shared = new ComicCoverResult { ArchivePath = "\\\\shared\\x.cbr", ArchiveFileName = "x.cbr" };
        var fakeService = new FakeSearchService(new List<ComicCoverResult>());
        fakeService.SetResultsForDirectory(dirA, new List<ComicCoverResult>
        {
            new ComicCoverResult { ArchivePath = dirA + "\\a1.cbr", ArchiveFileName = "a1.cbr", SimilarityScore = 0.50f },
            new ComicCoverResult { ArchivePath = shared.ArchivePath, ArchiveFileName = "x.cbr", SimilarityScore = 0.40f }
        });
        fakeService.SetResultsForDirectory(dirB, new List<ComicCoverResult>
        {
            new ComicCoverResult { ArchivePath = dirB + "\\b1.cbr", ArchiveFileName = "b1.cbr", SimilarityScore = 0.60f },
            // the same archive reachable through both roots — must not be doubled
            new ComicCoverResult { ArchivePath = shared.ArchivePath, ArchiveFileName = "x.cbr", SimilarityScore = 0.45f }
        });

        var settings = new FakeUserSettingsStore();
        var vm = CreateVmWithDirectories(fakeService, settings, dirA, dirB);
        vm.QueryText = "q";
        await RunSearch(vm);

        // Merged, de-duplicated (first occurrence wins) and ranked globally.
        Assert.Equal(3, vm.Results.Count);
        Assert.Equal(dirB + "\\b1.cbr", vm.Results[0].ArchivePath);   // 0.60
        Assert.Equal(dirA + "\\a1.cbr", vm.Results[1].ArchivePath);   // 0.50
        Assert.Equal(shared.ArchivePath, vm.Results[2].ArchivePath);   // 0.40 (dir A's copy kept)
        Assert.Equal(2, fakeService.SearchCalls); // one service call per root
        // The directory list is persisted to the settings.
        Assert.Equal(new[] { dirA, dirB }, settings.SearchDirectories);
    }

    [Fact]
    public async Task Search_WithNoDirectories_CancelsBeforeAnyServiceCall()
    {
        var fakeService = new FakeSearchService(new List<ComicCoverResult>());
        var vm = CreateVmWithDirectories(fakeService, new FakeUserSettingsStore());
        vm.QueryText = "q";
        await RunSearch(vm);

        Assert.Empty(vm.Results);
        Assert.Equal(0, fakeService.SearchCalls);
        Assert.Contains("add a search directory", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static ComicCoverSearchViewModel CreateVmWithDirectories(FakeSearchService fakeService,
        FakeUserSettingsStore settings, params string[] directories)
    {
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(
            NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance,
            new FakeUserSettingsStore { ThumbnailWidth = 64, ThumbnailHeight = 96 }, fakeService);
        var copyService = new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance);
        var vm = new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(),
            NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService, settings);
        foreach (var directory in directories)
        {
            vm.SearchDirectories.Add(directory);
        }

        return vm;
    }

    private static Task RunSearch(ComicCoverSearchViewModel vm)
    {
        var mi = typeof(ComicCoverSearchViewModel).GetMethod("SearchAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (Task)mi.Invoke(vm, null)!;
    }

    [Fact]
    public void Constructor_RestoresPhraseHistoryFromSettings()
    {
        var settings = new FakeUserSettingsStore { SearchPhraseHistory = new List<string> { "b", "a" } };
        var fakeService = new FakeSearchService(new List<ComicCoverResult>());
        var vm = CreateVmWithDirectories(fakeService, settings);

        Assert.Equal(new[] { "b", "a" }, vm.PhraseHistory.ToArray());
    }

    [Fact]
    public async Task Search_RecordsLaunchedQuery_AtFrontOfHistory()
    {
        var settings = new FakeUserSettingsStore();
        var fakeService = new FakeSearchService(new List<ComicCoverResult>());
        var vm = CreateVmWithDirectories(fakeService, settings, System.IO.Directory.GetCurrentDirectory());
        vm.QueryText = "beach";
        await RunSearch(vm);

        Assert.Equal(new[] { "beach" }, vm.PhraseHistory.ToArray());
        Assert.Equal(new[] { "beach" }, settings.SearchPhraseHistory);
    }

    [Fact]
    public async Task Search_RepeatedQuery_MovesToFrontWithoutDuplicating()
    {
        var settings = new FakeUserSettingsStore();
        var fakeService = new FakeSearchService(new List<ComicCoverResult>());
        var vm = CreateVmWithDirectories(fakeService, settings, System.IO.Directory.GetCurrentDirectory());

        vm.QueryText = "a";
        await RunSearch(vm);
        vm.QueryText = "b";
        await RunSearch(vm);
        vm.QueryText = "a";
        await RunSearch(vm);

        Assert.Equal(new[] { "a", "b" }, vm.PhraseHistory.ToArray());
        Assert.Equal(new[] { "a", "b" }, settings.SearchPhraseHistory);
    }

    [Fact]
    public async Task Search_ElevenDifferentQueries_HistoryCappedAt10()
    {
        var settings = new FakeUserSettingsStore();
        var fakeService = new FakeSearchService(new List<ComicCoverResult>());
        var vm = CreateVmWithDirectories(fakeService, settings, System.IO.Directory.GetCurrentDirectory());

        for (var i = 0; i < 11; i++)
        {
            vm.QueryText = $"query{i}";
            await RunSearch(vm);
        }

        Assert.Equal(10, vm.PhraseHistory.Count);
        Assert.Equal("query10", vm.PhraseHistory.First());
        Assert.DoesNotContain("query0", vm.PhraseHistory);
        Assert.Equal(10, settings.SearchPhraseHistory.Count);
    }

    [Fact]
    public void SelectingPhrase_FillsQueryText_AndDropdownMirrorsBox()
    {
        var settings = new FakeUserSettingsStore { SearchPhraseHistory = new List<string> { "x", "y" } };
        var fakeService = new FakeSearchService(new List<ComicCoverResult>());
        var vm = CreateVmWithDirectories(fakeService, settings);

        vm.SelectedPhrase = "x";

        Assert.Equal("x", vm.QueryText);
        Assert.Equal("x", vm.SelectedPhrase);
    }

    [Fact]
    public void EditingQueryText_ClearsSelectedPhrase()
    {
        var settings = new FakeUserSettingsStore { SearchPhraseHistory = new List<string> { "x" } };
        var fakeService = new FakeSearchService(new List<ComicCoverResult>());
        var vm = CreateVmWithDirectories(fakeService, settings);

        vm.SelectedPhrase = "x";
        vm.QueryText = "xy";

        Assert.Null(vm.SelectedPhrase);
    }

    [Fact]
    public void ReSelectingPhrase_AfterManualEdit_FillsBoxAgain()
    {
        var settings = new FakeUserSettingsStore { SearchPhraseHistory = new List<string> { "x" } };
        var fakeService = new FakeSearchService(new List<ComicCoverResult>());
        var vm = CreateVmWithDirectories(fakeService, settings);

        vm.SelectedPhrase = "x";
        vm.QueryText = "xy";      // clears the selection
        vm.SelectedPhrase = "x";  // select again

        Assert.Equal("x", vm.QueryText);
    }

    private static ComicCoverSearchViewModel CreateModeVm(FakeUserSettingsStore settings)
    {
        var fakeService = new FakeSearchService(new List<ComicCoverResult>());
        var thumbnailProvider = new ImageClassification.UI.Services.ThumbnailProvider(
            NullLogger<ImageClassification.UI.Services.ThumbnailProvider>.Instance, settings, fakeService);
        var copyService = new ImageCopyService(Mock.Of<IArchiveReader>(), NullLogger<ImageCopyService>.Instance);
        return new ComicCoverSearchViewModel(fakeService, Mock.Of<IVectorStore>(),
            NullLogger<ComicCoverSearchViewModel>.Instance, thumbnailProvider, copyService, settings);
    }
}
