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
public partial class ComicCoverSearchViewModel : ObservableObject, ITabMenuProvider
{
    private readonly IComicCoverSearchService _searchService;
    private readonly ILogger<ComicCoverSearchViewModel> _logger;
    private readonly ThumbnailProvider _thumbnailProvider;
    private readonly ImageCopyService _copyService;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _copyCts;
    private SearchMode _lastSearchMode = SearchMode.CoversOnly;
    private DateTime _lastProgressUiUpdateUtc = DateTime.MinValue;
    private string _currentPhase = string.Empty;
    private bool _showingDetail;

    /// <summary>
    /// Generation stamp for UI updates. Every progress/status callback captures the
    /// run id it belongs to and is dropped if the run has ended (finished, failed or
    /// cancelled) or a new run started — otherwise late dispatcher callbacks would
    /// overwrite "Search cancelled." with a stale phase/counter.
    /// </summary>
    private int _uiUpdateRunId;

    public ComicCoverSearchViewModel(IComicCoverSearchService searchService, ILogger<ComicCoverSearchViewModel> logger,
        ThumbnailProvider thumbnailProvider, ImageCopyService copyService)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _thumbnailProvider = thumbnailProvider;
        _copyService = copyService ?? throw new ArgumentNullException(nameof(copyService));

        SearchModes = new ObservableCollection<string>
        {
            "Covers only",
            "All pages"
        };

        Results.CollectionChanged += (_, _) => CopyResultsImagesCommand.NotifyCanExecuteChanged();
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
        //_logger.LogDebug(
        //    "VM ProgressCurrent changed: {Current}, ProgressTotal={Total}, Percent={Percent:F2}, IsBusy={IsBusy}",
        //    value, ProgressTotal, ProgressPercent, IsBusy);
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

    /// <summary>
    /// Rebuilds the status line from the current phase and (if known) the progress counter:
    /// "Processing archives...: 48/142", or just the phase while the total is unknown.
    /// (Full detail lines bypass this method — they are applied verbatim.)
    /// </summary>
    private void RefreshStatusMessage()
    {
        StatusMessage = ProgressTotal > 0
            ? $"{_currentPhase}: {ProgressCurrent}/{ProgressTotal}"
            : _currentPhase;
    }

    public ObservableCollection<ComicCoverItem> Results { get; } = new();

    public ObservableCollection<string> SearchModes { get; }

    /// <inheritdoc />
    public IReadOnlyList<TabMenuItem> MenuItems =>
        new[] { new TabMenuItem("Copy results images...", CopyResultsImagesCommand) };


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
        _showingDetail = false;
        _logger.LogInformation("SearchAsync started. ModeIndex={ModeIndex}, Directory='{Directory}', QueryLength={QueryLength}",
            SelectedModeIndex, DirectoryPath, QueryText?.Length ?? 0);

        // Validate inputs
        if (string.IsNullOrWhiteSpace(DirectoryPath))
        {
            StatusMessage = "Please select a directory with comic archives first.";
            return;
        }

        if (!System.IO.Directory.Exists(DirectoryPath))
        {
            StatusMessage = $"Directory not found: {DirectoryPath}";
            return;
        }

        if (string.IsNullOrWhiteSpace(QueryText))
        {
            StatusMessage = "Please enter a search query (e.g. 'beach', 'summer').";
            return;
        }

        IsBusy = true;
        int runId = Interlocked.Increment(ref _uiUpdateRunId);
        _currentPhase = "Preparing search...";
        StatusMessage = "Preparing search...";
        _thumbnailProvider.Clear();
        Results.Clear();
        ProgressCurrent = 0;
        ProgressTotal = 0;

        // Capture UI dispatcher and current mode BEFORE any await,
        // so Progress<T> is constructed on the UI thread and captures WPF SynchronizationContext.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        var currentMode = CurrentMode;

        // Unified status channel: phase markers, preformatted detail lines (per-archive /
        // GPU-queue snapshots) and progress-bar ticks all arrive as ComicSearchStatus.
        // Phase changes and detail lines are applied immediately; plain counter ticks
        // update the bar freely, but the status-line refresh is rate-limited to 250 ms
        // so a burst of archive events can't flood the UI thread.
        var status = new Progress<ComicSearchStatus>(s =>
        {
            void ApplyUpdate()
            {
                if (runId != Volatile.Read(ref _uiUpdateRunId)) return; // stale run

                if (s.Detail is not null)
                {
                    // Full detail line — shown verbatim until the next phase/detail update
                    if (s.Current is int current) ProgressCurrent = current;
                    if (s.Total is int total) ProgressTotal = total;
                    _showingDetail = true;
                    StatusMessage = s.Detail;
                    _logger.LogDebug("StatusMessage set: '{StatusMessage}'",
                        StatusMessage);
                    return;
                }

                if (!string.IsNullOrEmpty(s.Phase))
                {
                    // Phase marker — applied immediately; resets the counter-tick throttle
                    // so the next tick refreshes the line without delay
                    _showingDetail = false;
                    _currentPhase = s.Phase;
                    _lastProgressUiUpdateUtc = DateTime.MinValue;
                    RefreshStatusMessage();
                    _logger.LogDebug("Status phase: '{Phase}' ({Current}/{Total})",
                        s.Phase, ProgressCurrent, ProgressTotal);
                    return;
                }

                // Plain counter tick — bar updates freely, status-line refresh is throttled
                if (s.Current is int c) ProgressCurrent = c;
                if (s.Total is int t) ProgressTotal = t;
                if (_showingDetail) return; // keep the latest detail line on screen

                var now = DateTime.UtcNow;
                if (_lastProgressUiUpdateUtc == DateTime.MinValue || (now - _lastProgressUiUpdateUtc).TotalMilliseconds >= 250)
                {
                    RefreshStatusMessage();
                    _logger.LogDebug("StatusMessage set: '{StatusMessage}'",
                        StatusMessage);
                    _lastProgressUiUpdateUtc = now;
                }
            }

            // Always marshal to UI thread — defend against callers that invoke
            // status.Report from a thread-pool thread bypassing SynchronizationContext.
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
                _currentPhase = "Mode changed — clearing vector cache...";
                StatusMessage = "Mode changed — clearing vector cache...";
                await _searchService.ClearCacheAsync();
                _lastSearchMode = currentMode;
            }

            // Run on thread-pool so the UI thread is never blocked by IO or CPU work
            // (ScanArchivesRecursive, text-encoder init, GPU inference all happen off UI).
            // Progress<T> was constructed on UI thread and already marshals via Dispatcher.
            List<ComicCoverResult> results = await Task.Run(async () =>
            {
                if (currentMode == SearchMode.AllPages)
                    return await _searchService.SearchAllPagesAsync(DirectoryPath, QueryText, status, ct).ConfigureAwait(false);
                else
                    return await _searchService.SearchAsync(DirectoryPath, QueryText, status, ct).ConfigureAwait(false);
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
                    SimilarityScore = result.SimilarityScore,
                    PageIndex = result.PageIndex,
                    PageCount = result.PageCount
                };

                // Extract cover in memory and set the thumbnail when loaded
                _thumbnailProvider.EnsureCoverAndEnqueue(result.ArchivePath, b => item.Thumbnail = b);
 
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
            else if (results.Count == 0)
            {
                // No results at all — keep the service's final phase if it explains why
                // (e.g. "No comic archives found in the selected directory").
                StatusMessage = string.IsNullOrWhiteSpace(_currentPhase)
                    ? "No matching results found."
                    : _currentPhase;
            }
            else
            {
                StatusMessage = "No results above the similarity threshold.";
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
            // Invalidate late progress/status callbacks from this run
            Interlocked.Increment(ref _uiUpdateRunId);
            IsBusy = false;
        }
    }

    private bool CanSearch() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        _logger.LogInformation("IsBusy changed to {IsBusy}. Search button should be {State}",
            value, value ? "disabled" : "enabled");
        SearchCommand.NotifyCanExecuteChanged();
        CopyResultsImagesCommand.NotifyCanExecuteChanged();
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
        if (_copyCts is not null)
        {
            _copyCts.Cancel();
            StatusMessage = "Cancelling copy...";
        }
        else
        {
            _cts?.Cancel();
            StatusMessage = "Cancelling...";
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopyResults))]
    private async Task CopyResultsImagesAsync()
    {
        if (Results.Count == 0)
        {
            StatusMessage = "No results to copy.";
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Select destination folder for copied images"
        };
        if (dialog.ShowDialog() != true)
            return;

        await CopyResultsImagesCoreAsync(dialog.FolderName);
    }

    /// <summary>
    /// Copies all result images (extracted in memory, no temp files) into <paramref name="destinationDirectory"/>.
    /// </summary>
    public async Task CopyResultsImagesCoreAsync(string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            StatusMessage = "No destination folder selected.";
            return;
        }

        var items = Results.ToList();
        if (items.Count == 0)
        {
            StatusMessage = "No results to copy.";
            return;
        }

        IsBusy = true;
        int copyRunId = Interlocked.Increment(ref _uiUpdateRunId);
        StatusMessage = "Copying images...";
        ProgressCurrent = 0;
        ProgressTotal = items.Count;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        var progress = new Progress<int>(p =>
        {
            void ApplyUpdate()
            {
                if (copyRunId != Volatile.Read(ref _uiUpdateRunId)) return; // stale run

                ProgressCurrent = p;
                StatusMessage = $"Copying images: {p}/{items.Count}";
            }

            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(ApplyUpdate);
            else
                ApplyUpdate();
        });

        try
        {
            _copyCts?.Cancel();
            _copyCts = new CancellationTokenSource();
            var ct = _copyCts.Token;

            CopySummary summary = await Task.Run(() =>
                _copyService.CopyImagesAsync(items, destinationDirectory, progress, ct), ct);

            var parts = new List<string> { $"Copied {summary.Copied} of {items.Count} images to {destinationDirectory}" };
            if (summary.Skipped > 0)
                parts.Add($"skipped {summary.Skipped}");
            StatusMessage = string.Join(", ", parts);
            if (summary.Errors.Count > 0)
            {
                _logger.LogWarning("Copy finished with {ErrorCount} errors: {Errors}",
                    summary.Errors.Count, string.Join("; ", summary.Errors.Take(10)));
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Copy cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy failed: {Message}", ex.Message);
            StatusMessage = $"Copy failed: {ex.Message}";
        }
        finally
        {
            _copyCts = null;
            Interlocked.Increment(ref _uiUpdateRunId);
            IsBusy = false;
        }
    }

    private bool CanCopyResults() => !IsBusy && Results.Count > 0;

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