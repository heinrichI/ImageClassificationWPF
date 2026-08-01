using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
using ImageClassification.UI.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ImageClassification.UI.ViewModels;

/// <summary>
/// Pure ViewModel for the Comic Cover Search tab.
/// No code-behind logic — all commands and bindings are here.
/// </summary>
public partial class ComicCoverSearchViewModel : ObservableObject
{
    private readonly IComicCoverSearchService _searchService;
    private readonly ILogger<ComicCoverSearchViewModel> _logger;
    private readonly ThumbnailProvider _thumbnailProvider;
    private CancellationTokenSource? _cts;
    private SearchMode _lastSearchMode = SearchMode.CoversOnly;
    private DateTime _lastProgressUiUpdateUtc = DateTime.MinValue;

    public ComicCoverSearchViewModel(IComicCoverSearchService searchService, ILogger<ComicCoverSearchViewModel> logger,
        ThumbnailProvider thumbnailProvider)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _thumbnailProvider = thumbnailProvider;

        SearchModes = new ObservableCollection<string>
        {
            "Covers only",
            "All pages"
        };
    }

    // ────────────────────────────── Observable properties ──────────────────────────────

    [ObservableProperty]
    private string _directoryPath = string.Empty;

    [ObservableProperty]
    private string _queryText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private double _similarityThreshold = 0.25;

    [ObservableProperty]
    private int _progressCurrent;
    partial void OnProgressCurrentChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressPercent));
        _logger.LogDebug(
            "VM ProgressCurrent changed: {Current}, ProgressTotal={Total}, Percent={Percent:F2}, IsBusy={IsBusy}",
            value, ProgressTotal, ProgressPercent, IsBusy);
    }

    [ObservableProperty]
    private int _progressTotal;
    partial void OnProgressTotalChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressPercent));
        _logger.LogDebug(
            "VM ProgressTotal changed: {Total}, ProgressCurrent={Current}, Percent={Percent:F2}, IsBusy={IsBusy}",
            value, ProgressCurrent, ProgressPercent, IsBusy);
    }

    /// <summary>
    /// Index into the SearchModes collection, bound to the ComboBox SelectedIndex.
    /// 0 = CoversOnly, 1 = AllPages.
    /// </summary>
    [ObservableProperty]
    private int _selectedModeIndex;

    public float ProgressPercent => ProgressTotal > 0
        ? (ProgressCurrent / (float)ProgressTotal) * 100f
        : 0f;

    public ObservableCollection<ComicCoverItem> Results { get; } = new();

    public ObservableCollection<string> SearchModes { get; }

    /// <summary>
    /// Returns the current SearchMode derived from SelectedModeIndex.
    /// </summary>
    private SearchMode CurrentMode => (SearchMode)SelectedModeIndex;

    // ────────────────────────────── Commands ──────────────────────────────

    [RelayCommand]
    private void BrowseDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select directory with comic archives (CBZ/CBR)"
        };

        if (dialog.ShowDialog() == true)
        {
            DirectoryPath = dialog.FolderName;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSearch), AllowConcurrentExecutions = false)]
    private async Task SearchAsync()
    {
        if (IsBusy)
        {
            _logger.LogWarning("SearchAsync ignored because IsBusy=true");
            return;
        }

        _lastProgressUiUpdateUtc = DateTime.MinValue;
        _logger.LogInformation("SearchAsync started. ModeIndex={ModeIndex}, Directory='{Directory}', QueryLength={QueryLength}",
            SelectedModeIndex, DirectoryPath, QueryText?.Length ?? 0);

        // Validate inputs
        if (string.IsNullOrWhiteSpace(DirectoryPath))
        {
            StatusMessage = "Please select a directory with comic archives first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(QueryText))
        {
            StatusMessage = "Please enter a search query (e.g. 'beach', 'summer').";
            return;
        }

        IsBusy = true;
        StatusMessage = "Searching...";
        _thumbnailProvider.Clear();
        Results.Clear();
        ProgressCurrent = 0;
        ProgressTotal = 0;

        // Capture UI dispatcher and current mode BEFORE any await,
        // so Progress<T> is constructed on the UI thread and captures WPF SynchronizationContext.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        var currentMode = CurrentMode;

        // Build the progress reporter while still on the UI thread.
        var progress = new Progress<(int Current, int Total)>(p =>
        {
            //_logger.LogInformation("Progress callback received: {Current}/{Total}, IsBusy={IsBusy}, ThreadId={ThreadId}",
            //    p.Current, p.Total, IsBusy, Environment.CurrentManagedThreadId);

            void ApplyUpdate()
            {
                ProgressTotal   = p.Total;
                ProgressCurrent = p.Current;

                var now = DateTime.UtcNow;
                if (_lastProgressUiUpdateUtc == DateTime.MinValue || (now - _lastProgressUiUpdateUtc).TotalMilliseconds >= 250)
                {
                    var modeText = currentMode == SearchMode.AllPages ? "pages" : "covers";
                    StatusMessage = $"Processing {modeText}: {p.Current}/{p.Total}";
                    _logger.LogInformation("StatusMessage set: '{StatusMessage}', Current={Current}, Total={Total}",
                        StatusMessage, p.Current, p.Total);
                    _lastProgressUiUpdateUtc = now;
                }
            }

            // Always marshal to UI thread — defend against callers that invoke
            // progress.Report from a thread-pool thread bypassing SynchronizationContext.
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(ApplyUpdate);
            else
                ApplyUpdate();
        });

        try
        {
            // Cancel any previous operation
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            // Clear cache if mode changed since last search
            if (_lastSearchMode != currentMode)
            {
                await _searchService.ClearCacheAsync();
                _lastSearchMode = currentMode;
            }

            // Run on thread-pool so the UI thread is never blocked by IO or CPU work
            // (ScanArchivesRecursive, text-encoder init, GPU inference all happen off UI).
            // Progress<T> was constructed on UI thread and already marshals via Dispatcher.
            List<ComicCoverResult> results = await Task.Run(async () =>
            {
                if (currentMode == SearchMode.AllPages)
                    return await _searchService.SearchAllPagesAsync(DirectoryPath, QueryText, progress, ct).ConfigureAwait(false);
                else
                    return await _searchService.SearchAsync(DirectoryPath, QueryText, progress, ct).ConfigureAwait(false);
            }, ct);

            // Filter by similarity threshold
            var filtered = results.Where(r => r.SimilarityScore >= SimilarityThreshold).ToList();

            _logger.LogDebug($"Populate the observable collection {filtered.Count}");
            // Populate the observable collection and trigger thumbnail loads
            foreach (var result in filtered)
            {
                var item = new ComicCoverItem
                {
                    ArchivePath = result.ArchivePath,
                    ArchiveFileName = result.ArchiveFileName,
                    CoverImagePath = result.CoverImagePath,
                    SimilarityScore = result.SimilarityScore,
                    PageIndex = result.PageIndex,
                    PageCount = result.PageCount
                };

                // Trigger thumbnail load if we have a cover image path; otherwise request extraction
                if (!string.IsNullOrEmpty(result.CoverImagePath))
                {
                    var bitmap = _thumbnailProvider.GetBitmap(result.CoverImagePath, 180, b => item.Thumbnail = b);
                    if (bitmap != null) item.Thumbnail = bitmap;
                }
                else
                {
                    // Start extraction; update item's CoverImagePath and set thumbnail when loaded
                    _thumbnailProvider.EnsureCoverAndEnqueue(
                        result.ArchivePath,
                        coverPath =>
                        {
                            if (!string.IsNullOrEmpty(coverPath))
                                item.CoverImagePath = coverPath;
                        },
                        b => item.Thumbnail = b);
                }
 
                // Add to results (thumbnail callback will update UI when available)
                Results.Add(item);
            }

            if (Results.Count > 0)
            {
                var modeText = currentMode == SearchMode.AllPages ? "pages" : "covers";
                StatusMessage = $"Found {Results.Count} matching {modeText} (filtered from {results.Count} total, " +
                    $"scores: {filtered.Min(r => r.SimilarityScore):P1}–{filtered.Max(r => r.SimilarityScore):P1}, " +
                    $"median: {filtered.OrderBy(r => r.SimilarityScore).ElementAt(filtered.Count / 2).SimilarityScore:P1})";
            }
            else
            {
                StatusMessage = "No matching results found.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Search cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed: {Message}", ex.Message);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSearch() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        _logger.LogInformation("IsBusy changed to {IsBusy}. Search button should be {State}",
            value, value ? "disabled" : "enabled");
        SearchCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void OpenComic(string? archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(archivePath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot open file {ArchivePath}: {Message}", archivePath, ex.Message);
            StatusMessage = $"Cannot open file: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusMessage = "Cancelling...";
    }

    [RelayCommand]
    private void CopyArchivePath(string? archivePath)
    {
        if (!string.IsNullOrWhiteSpace(archivePath))
        {
            try
            {
                Clipboard.SetText(archivePath);
                StatusMessage = $"Copied: {archivePath}";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cannot copy to clipboard: {Message}", ex.Message);
                StatusMessage = $"Cannot copy to clipboard: {ex.Message}";
            }
        }
    }
}

/// <summary>
/// ViewModel item for a single comic cover result in the list.
/// </summary>
public class ComicCoverItem : ObservableObject
{
    private string _archivePath = string.Empty;
    public string ArchivePath
    {
        get => _archivePath;
        set => SetProperty(ref _archivePath, value);
    }

    private string _archiveFileName = string.Empty;
    public string ArchiveFileName
    {
        get => _archiveFileName;
        set => SetProperty(ref _archiveFileName, value);
    }

    private string _coverImagePath = string.Empty;
    public string CoverImagePath
    {
        get => _coverImagePath;
        set => SetProperty(ref _coverImagePath, value);
    }

    private BitmapSource? _thumbnail;
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    private float _similarityScore;
    public float SimilarityScore
    {
        get => _similarityScore;
        set => SetProperty(ref _similarityScore, value);
    }

    public string SimilarityText => $"{SimilarityScore:P1}";

    private int _pageIndex;
    public int PageIndex
    {
        get => _pageIndex;
        set => SetProperty(ref _pageIndex, value);
    }

    private int _pageCount;
    public int PageCount
    {
        get => _pageCount;
        set => SetProperty(ref _pageCount, value);
    }

    public string PageLabel => PageCount > 0 ? $"Page {PageIndex + 1} of {PageCount}" : string.Empty;
}