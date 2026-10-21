using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
using ImageClassification.UI.Configuration;
using ImageClassification.UI.Models;
using ImageClassification.UI.Services;
using ImageClassification.UI.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ImageClassification.UI.ViewModels;

/// <summary>
/// Pure ViewModel for the Search Image tab (CLIP text→image search over plain image
/// files in user-selected directories). No code-behind logic — all commands and
/// bindings are here. Cloned from ComicCoverSearchViewModel's surface (directories,
/// phrase history, threshold, progress, taskbar, notes, DB commands) and adapted:
/// the pipeline is per-file (no GPU batches), thumbnails come from the shared
/// ThumbnailProvider, and copy uses <c>ImageCopyService.CopyFilesAsync</c>.
/// </summary>
public partial class SearchImageViewModel : ObservableObject, ITabMenuProvider, IThumbnailRequester
{
    private readonly IImageSearchService _searchService;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<SearchImageViewModel> _logger;
    private readonly ThumbnailProvider _thumbnailProvider;
    private readonly ImageCopyService _copyService;
    private readonly IUserSettingsStore _settings;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _copyCts;
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

    public SearchImageViewModel(
        IImageSearchService searchService,
        IVectorStore vectorStore,
        ILogger<SearchImageViewModel> logger,
        ThumbnailProvider thumbnailProvider,
        ImageCopyService copyService,
        IUserSettingsStore settings)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _vectorStore = vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _thumbnailProvider = thumbnailProvider;
        _copyService = copyService ?? throw new ArgumentNullException(nameof(copyService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        // Live thumbnail pipeline status (which file is being read, queue, loaded total).
        // The provider is shared with the Comic Search tab, so each tab subscribes its own
        // handler; the event can fire on a worker thread and is marshaled to the UI.
        _thumbnailProvider.StatusChanged += OnThumbnailPipelineStatus;

        // Restore persisted state: directories, phrase history, threshold, recursion option.
        // (The guards in On~Changed keep this restore from rewriting the settings file.)
        SearchDirectories = new ObservableCollection<string>(settings.ImageSearchDirectories);
        PhraseHistory = new ObservableCollection<string>(settings.ImageSearchPhraseHistory);
        SimilarityThreshold = Math.Clamp(settings.ImageSearchSimilarityThreshold, 0d, 1d);
        RecurseSubfolders = settings.ImageSearchRecurseSubfolders;

        Results.CollectionChanged += OnResultsCollectionChanged;
    }

    /// <summary>
    /// Requests (or bumps) the thumbnail of <paramref name="item"/> — called by the item
    /// template's LoadThumbnailOnVisibleBehavior the moment the tile becomes visible. The
    /// provider keeps per-tile pipeline state, so a tile that is already in flight (or
    /// loaded) is NOT re-decoded: the request moves it to the front of whichever queue it
    /// sits in, making the on-screen tile load first.
    /// </summary>
    public void RequestThumbnail(object? item)
    {
        if (item is SearchImageItem searchItem)
            QueueImageThumbnail(searchItem);
    }

    private void QueueImageThumbnail(SearchImageItem item)
    {
        _thumbnailProvider.Enqueue(item.FilePath, _settings.ThumbnailWidth,
            r =>
            {
                if (r is null) return;
                var res = r.Value;
                if (res.Image is not null) item.Thumbnail = res.Image;
                item.InfoText = TileInfoText.Format(res.Width, res.Height, res.ByteSize);
            });
    }

    /// <summary>
    /// Re-subscribes the collection-changed handler when the Results collection instance is
    /// replaced (bulk population after a search). Also refreshes the derived notifications,
    /// which the old instance's final Clear/Reset has already covered for the old data.
    /// </summary>
    partial void OnResultsChanged(ObservableCollection<SearchImageItem>? oldValue, ObservableCollection<SearchImageItem> newValue)
    {
        if (oldValue is not null)
            oldValue.CollectionChanged -= OnResultsCollectionChanged;
        newValue.CollectionChanged += OnResultsCollectionChanged;

        CopyResultsImagesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ResultsCountText));
    }

    private void OnResultsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        CopyResultsImagesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ResultsCountText));
    }

    // ────────────────────────────── Observable properties ──────────────────────────────

    /// <summary>
    /// Search root directories (persisted — a fresh process restores the list, so the user
    /// doesn't re-select them on every launch). Every directory is scanned for image files
    /// (subfolders included when <see cref="RecurseSubfolders"/> is on); results from all
    /// roots are merged and de-duplicated.
    /// </summary>
    public ObservableCollection<string> SearchDirectories { get; }

    /// <summary>
    /// The last launched search phrases (newest first, max 10) — restored from the
    /// settings so the dropdown above the query box survives a restart.
    /// </summary>
    public ObservableCollection<string> PhraseHistory { get; }

    [ObservableProperty]
    private string _queryText = string.Empty;

    /// <summary>
    /// The phrase picked from the history dropdown. Fills <see cref="QueryText"/>; a
    /// manual edit of the query box clears it so the dropdown stops mirroring stale text.
    /// </summary>
    [ObservableProperty]
    private string? _selectedPhrase;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _thumbnailStatus = string.Empty;

    /// <summary>Full path of the image the pipeline started processing most recently (empty when idle).</summary>
    [ObservableProperty]
    private string _currentFile = string.Empty;

    /// <summary>
    /// Minimum similarity score for the results (0..1). The user moved the threshold
    /// control — persist it so a fresh process starts with the same result filter.
    /// </summary>
    [ObservableProperty]
    private double _similarityThreshold = 0.25;

    partial void OnSimilarityThresholdChanged(double value)
    {
        if (Math.Abs(value - _settings.ImageSearchSimilarityThreshold) < 1e-9)
            return;
        _settings.SaveImageSearchSimilarityThreshold(value);
    }

    /// <summary>
    /// Include files from subdirectories of the selected roots. Persisted so a fresh
    /// process starts with the same scan scope.
    /// </summary>
    [ObservableProperty]
    private bool _recurseSubfolders = true;

    partial void OnRecurseSubfoldersChanged(bool value)
    {
        if (value == _settings.ImageSearchRecurseSubfolders)
            return;
        _settings.SaveImageSearchRecurseSubfolders(value);
    }

    [ObservableProperty]
    private int _progressCurrent;
    partial void OnProgressCurrentChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressPercent));
    }

    [ObservableProperty]
    private int _progressTotal;
    partial void OnProgressTotalChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressPercent));
    }

    /// <summary>
    /// Index of the inner panel inside the Search Image tab: 0 = Search settings,
    /// 1 = Results. Bound to the inner TabControl SelectedIndex. The VM switches to
    /// the search panel when a run starts and to the results panel when it finishes
    /// with at least one match.
    /// </summary>
    [ObservableProperty]
    private int _selectedInnerTabIndex;

    public float ProgressPercent => ProgressTotal > 0
        ? (ProgressCurrent / (float)ProgressTotal) * 100f
        : 0f;

    // ── Windows taskbar progress (both ProgressState and ProgressValue are dependency
    //    properties on TaskbarItemInfo; MainViewModel merges both search tabs into the
    //    single window taskbar binding) ──

    [ObservableProperty]
    private double _taskbarProgressValue;

    [ObservableProperty]
    private TaskbarItemProgressState _taskbarProgressState = TaskbarItemProgressState.None;

    /// <summary>
    /// Mirrors search progress on the Windows taskbar button: determinate bar while the
    /// file total is known, animated stripes while it is not (query encoding), hidden
    /// when idle. TaskbarItemInfo.ProgressValue is coerced by WPF to the range 0.0–1.0.
    /// </summary>
    private void UpdateTaskbar()
    {
        TaskbarProgressState = !IsBusy
            ? TaskbarItemProgressState.None
            : ProgressTotal > 0
                ? TaskbarItemProgressState.Normal
                : TaskbarItemProgressState.Indeterminate;

        TaskbarProgressValue = IsBusy && ProgressTotal > 0
            ? ProgressCurrent / (double)ProgressTotal
            : 0;
    }

    /// <summary>
    /// Rebuilds the status line from the current phase and (if known) the progress counter:
    /// "Found 1234 images: 48/1234", or just the phase while the total is unknown.
    /// (Full detail lines bypass this method — they are applied verbatim.)
    /// </summary>
    private void RefreshStatusMessage()
    {
        StatusMessage = ProgressTotal > 0
            ? $"{_currentPhase}: {ProgressCurrent}/{ProgressTotal}"
            : _currentPhase;
    }

    [ObservableProperty]
    private ObservableCollection<SearchImageItem> _results = new();

    /// <summary>
    /// True when the Results collection holds at least one item.
    /// Drives the "no results yet" placeholder on the results panel.
    /// </summary>
    public bool HasResults => Results.Count > 0;

    /// <summary>
    /// Header text of the results tab: "Results (N)" while populated, plain "Results" otherwise.
    /// </summary>
    public string ResultsCountText => Results.Count > 0 ? $"Results ({Results.Count})" : "Results";

    /// <summary>
    /// Files that could not be encoded during the current search (corrupt/locked files) —
    /// accumulated in a visible UI list; cleared on search start. Capped so a pathological
    /// directory cannot grow it unboundedly.
    /// </summary>
    public ObservableCollection<string> FileNotes { get; } = new();

    // Guards the notes collection: in the app every mutation is dispatcher-marshalled,
    // but under tests (no WPF dispatcher) status updates can apply concurrently.
    private readonly object _notesLock = new();

    private const int MaxFileNotes = 500;

    /// <summary>Item count of <see cref="FileNotes"/> (drives the notes panel visibility).</summary>
    public int FileNotesCount { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<TabMenuItem> MenuItems =>
        new[]
        {
            new TabMenuItem("Copy", Children:
            [
                new TabMenuItem("Copy results images...", CopyResultsImagesCommand)
            ]),
            new TabMenuItem("DB", Children:
            [
                new TabMenuItem("Vector cache: Info...", VectorCacheInfoCommand),
                new TabMenuItem("Vector cache: Optimize", VectorCacheOptimizeCommand),
                new TabMenuItem("Vector cache: Vacuum...", VectorCacheVacuumCommand),
                new TabMenuItem("Vector cache: Prune missing files...", VectorCachePruneMissingFilesCommand)
            ])
        };

    // ────────────────────────────── Commands ──────────────────────────────

    [RelayCommand]
    private void AddDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select directory with images (JPG/PNG/BMP/TIFF/WebP)"
        };

        if (dialog.ShowDialog() == true && TryAddDirectory(dialog.FolderName))
            PersistDirectories();
    }

    /// <summary>
    /// Drag-and-drop entry point (folders dragged from Windows Explorer onto the
    /// directory panel — see DropDirectoriesBehavior): adds every valid,
    /// non-duplicate directory at once and persists the list.
    /// </summary>
    [RelayCommand]
    private void AddDirectories(string[]? directories)
    {
        if (directories is null)
            return;

        var added = false;
        foreach (var directory in directories)
            added |= TryAddDirectory(directory);

        if (added)
            PersistDirectories();
    }

    /// <summary>
    /// Adds a directory to the list when it exists and is not already present
    /// (case-insensitive, trailing-backslash-insensitive). Returns true when added.
    /// </summary>
    private bool TryAddDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        var path = DirectoryPaths.Normalize(directory);
        if (!System.IO.Directory.Exists(path))
            return false;

        if (DirectoryPaths.IsDuplicate(SearchDirectories, path))
            return false;

        SearchDirectories.Add(path);
        return true;
    }

    [RelayCommand]
    private void RemoveDirectory(string directory)
    {
        if (directory is not null && SearchDirectories.Remove(directory))
            PersistDirectories();
    }

    private void PersistDirectories() => _settings.SaveImageSearchDirectories(SearchDirectories.ToList());

    /// <summary>
    /// Records a launched query in the phrase history: the phrase moves (or is inserted)
    /// to the front — re-searching an existing phrase does not duplicate it — the list is
    /// truncated to the last 10 and persisted. The dropdown collection is refreshed in
    /// place so its binding picks up the change.
    /// </summary>
    private void RecordPhraseInHistory(string phrase)
    {
        // An editable ComboBox wipes its Text (which is bound TwoWay to QueryText)
        // when a *selected* item is removed — after a first search WPF auto-matches
        // the typed text to the now-first history item, so the Clear() below would
        // erase the user's query. Snapshot it and restore after the refresh.
        var query = QueryText;

        var updated = new List<string>(PhraseHistory.Count);
        updated.Add(phrase);
        foreach (var existing in PhraseHistory)
        {
            if (string.Equals(existing, phrase, StringComparison.OrdinalIgnoreCase))
                continue; // dedup: the phrase now sits at the front.
            updated.Add(existing);
            if (updated.Count == 10)
                break;
        }

        _settings.SaveImageSearchPhraseHistory(updated);

        bool unchanged = updated.Count == PhraseHistory.Count
            && updated.SequenceEqual(PhraseHistory, StringComparer.OrdinalIgnoreCase);
        if (unchanged)
            return; // no collection churn — the ComboBox text stays untouched

        PhraseHistory.Clear();
        foreach (var item in updated)
            PhraseHistory.Add(item);

        if (!string.Equals(QueryText, query, StringComparison.Ordinal))
            QueryText = query; // the ComboBox reset its text — put the query back
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
        _logger.LogInformation("SearchAsync started. Directories='{Directories}', Recurse={Recurse}, QueryLength={QueryLength}",
            string.Join("; ", SearchDirectories), RecurseSubfolders, QueryText?.Length ?? 0);

        // Validate inputs
        if (SearchDirectories.Count == 0)
        {
            StatusMessage = "Please add a search directory first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(QueryText))
        {
            StatusMessage = "Please enter a search query (e.g. 'autumn forest', 'beach').";
            return;
        }

        // The list the search runs with is what the user wants remembered.
        PersistDirectories();
        RecordPhraseInHistory(QueryText);

        IsBusy = true;
        SelectedInnerTabIndex = 0; // show the settings/progress panel while the search runs
        int runId = Interlocked.Increment(ref _uiUpdateRunId);
        _currentPhase = "Preparing search...";
        StatusMessage = "Preparing search...";
        UpdateTaskbar();
        _thumbnailProvider.Clear();
        Results.Clear();
        CurrentFile = string.Empty;
        lock (_notesLock)
        {
            FileNotes.Clear();
            FileNotesCount = 0;
        }
        ProgressCurrent = 0;
        ProgressTotal = 0;

        // Capture UI dispatcher and current options BEFORE any await,
        // so Progress<T> is constructed on the UI thread and captures WPF SynchronizationContext.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        var options = new ImageSearchOptions { RecurseSubfolders = RecurseSubfolders };

        // Unified status channel: phase markers and progress-bar ticks all arrive as
        // ImageSearchStatus. Phase changes are applied immediately; plain counter ticks
        // update the bar freely, but the status-line refresh is rate-limited to 250 ms
        // so a cache-hit burst cannot flood the UI thread.
        var status = new Progress<ImageSearchStatus>(s =>
        {
            void ApplyUpdate()
            {
                if (runId != Volatile.Read(ref _uiUpdateRunId)) return; // stale run

                // File diagnostics (rare per-search events) — accumulate in the notes list.
                if (s.Note is not null)
                {
                    lock (_notesLock)
                    {
                        FileNotes.Add(s.Note);
                        if (FileNotes.Count > MaxFileNotes)
                            FileNotes.RemoveAt(0);
                    }
                    FileNotesCount = FileNotes.Count;
                    OnPropertyChanged(nameof(FileNotesCount));
                }

                // "Currently processing" file label — the service rate-limits these reports.
                if (s.CurrentFile is not null)
                    CurrentFile = s.CurrentFile;

                // Refresh the taskbar button on every status event (cheap: two property writes)
                UpdateTaskbar();

                if (s.Detail is not null)
                {
                    // Full detail line — shown verbatim until the next phase update
                    if (s.Current is int current) ProgressCurrent = current;
                    if (s.Total is int total) ProgressTotal = total;
                    _showingDetail = true;
                    StatusMessage = s.Detail;
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

            // No cache clearing: the "ClipViTB32" entries are shared with the Analyze/By Tags
            // mode and are freshness-validated via mtime/size in the vector store, so a new
            // search reuses them as cheap hits.

            // Run on thread-pool so the UI thread is never blocked by IO or CPU work
            // (directory scan, per-file encoding all happen off UI).
            // Progress<T> was constructed on UI thread and already marshals via Dispatcher.
            IProgress<ImageSearchStatus> outerStatus = status; // interface view: Report is on IProgress
            List<ImageSearchResult> results = await Task.Run(async () =>
            {
                var merged = new List<ImageSearchResult>();
                var roots = SearchDirectories.ToList();
                for (var i = 0; i < roots.Count; i++)
                {
                    var dir = roots[i];

                    // Per-root phase marker: "Scanning images: <dir> (2/3)". During the
                    // root the service's notes carry the directory prefix.
                    outerStatus.Report(new ImageSearchStatus
                    {
                        Phase = $"Scanning images: {dir} ({i + 1}/{roots.Count})"
                    });

                    var rootStatus = new Progress<ImageSearchStatus>(s =>
                        outerStatus.Report(string.IsNullOrEmpty(s.Detail) ? s : s with { Detail = $"{dir} | {s.Detail}" }));

                    merged.AddRange(await _searchService.SearchAsync(dir, QueryText, options, rootStatus, ct).ConfigureAwait(false));
                }

                // Nested/overlapping roots can surface the same file twice — de-duplicate
                // by path keeping the first occurrence, then rank globally.
                return merged
                    .GroupBy(r => r.FilePath)
                    .Select(g => g.First())
                    .OrderByDescending(r => r.SimilarityScore)
                    .ToList();
            }, ct);

            // Filter by similarity threshold
            var filtered = results.Where(r => r.SimilarityScore >= SimilarityThreshold).ToList();

            // Pre-fetch only the FIRST tiles, up to the thumbnail cache limit: anything
            // beyond it would be evicted from the LRU cache before the user scrolls there,
            // so those tiles load on demand — the moment their tile becomes visible
            // (item template behavior → RequestThumbnail, a priority bump in the pipeline).
            // Pushed in reverse display order, so the LIFO pipeline fills the off-screen
            // zone from the top of the list down — the natural scroll direction.

            _logger.LogDebug($"Populate the observable collection {filtered.Count}");
            // Build items outside the observable and assign the collection in one go:
            // a single CollectionChanged(Reset) instead of N per-item Adds.
            var swPopulate = Stopwatch.StartNew();
            var items = new List<SearchImageItem>(filtered.Count);
            for (var i = 0; i < filtered.Count; i++)
            {
                var result = filtered[i];
                var item = new SearchImageItem
                {
                    FilePath = result.FilePath,
                    FileName = result.FileName,
                    SimilarityScore = result.SimilarityScore,
                    // keeps the image cell size stable until the preview is decoded; the
                    // real thumbnail is swapped in when the tile becomes visible
                    Thumbnail = _thumbnailProvider.GetPlaceholder()
                };

                items.Add(item);
            }

            // One bulk assignment → one CollectionChanged(Reset)
            Results = new ObservableCollection<SearchImageItem>(items);

            // Background pre-fetch of the first tiles (up to the cache limit; see the
            // note above the populate block).
            var prefetchCount = Math.Min(items.Count, _settings.MaxCachedThumbnails);
            for (var i = prefetchCount - 1; i >= 0; i--)
            {
                QueueImageThumbnail(items[i]);
            }

            _logger.LogInformation(
                "[Perf] Results populated: {Shown} item(s) (filtered from {Total}), in {Ms} ms on UI thread",
                items.Count, results.Count, swPopulate.ElapsedMilliseconds);

            if (Results.Count > 0)
            {
                StatusMessage = $"Found {Results.Count} matching images (filtered from {results.Count} total, " +
                    $"scores: {filtered.Min(r => r.SimilarityScore):P1}–{filtered.Max(r => r.SimilarityScore):P1}, " +
                    $"median: {filtered.OrderBy(r => r.SimilarityScore).ElementAt(filtered.Count / 2).SimilarityScore:P1})";
                SelectedInnerTabIndex = 1; // auto-switch to the full-height results panel
            }
            else if (results.Count == 0)
            {
                // No results at all — keep the service's final phase if it explains why
                // (e.g. "No images found in the selected directory"); when every root was
                // skipped (missing directory notes) the phase line is misleading.
                StatusMessage = FileNotes.Count > 0
                    ? "No images were processed — see notes."
                    : string.IsNullOrWhiteSpace(_currentPhase)
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
            CurrentFile = string.Empty;
            UpdateTaskbar();
        }
    }

    private bool CanSearch() => !IsBusy;

    // ─────────────────────── Vector cache menu commands ───────────────────────
    // The "ClipViTB32" image embeddings used by this tab live in the same cache DB as
    // the comic search / Analyze mode, so the same DB maintenance commands apply.

    [RelayCommand]
    private async Task VectorCacheInfoAsync()
    {
        VectorCacheInfo info;
        try
        {
            info = await _vectorStore.GetCacheInfoAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to read cache info: {ex.Message}",
                "Vector Cache", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var window = new VectorCacheInfoWindow(info);
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }

    [RelayCommand]
    private async Task VectorCacheOptimizeAsync()
    {
        var result = await _vectorStore.OptimizeAsync();
        MessageBox.Show(result.Detail, "Vector Cache — Optimize", MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    [RelayCommand]
    private void VectorCacheVacuum()
    {
        var window = new VectorCacheMaintenanceWindow(
            "Vector cache — Vacuum",
            "Rebuilds the database file from scratch, removing free-space holes left by prunes and updates.\n\n" +
            "The cache is locked while it runs: searches are unavailable until the operation finishes. " +
            "On a multi-gigabyte database this can take several minutes.\n\n" +
            "Interrupt stops the operation and rolls it back — the database is never corrupted.",
            (ct, progress) => _vectorStore.VacuumAsync(progress, ct));
        window.ShowDialog();
    }

    [RelayCommand]
    private void VectorCachePruneMissingFiles()
    {
        var window = new VectorCacheMaintenanceWindow(
            "Vector cache — Prune missing files",
            "Removes cache entries for files that no longer exist on disk.\n\n" +
            "Only disks that are currently connected are checked. Files on disconnected " +
            "drives (removable media, offline shares) are kept in the cache, since they " +
            "may reappear once the disk is reconnected.\n\n" +
            "Scanning a large cache can take a while — the window shows progress and a " +
            "Cancel button; cancelling at any point leaves the cache untouched.",
            (ct, progress) => _vectorStore.PruneMissingFilesAsync(null, progress, ct));
        window.ShowDialog();
    }

    partial void OnIsBusyChanged(bool value)
    {
        _logger.LogInformation("IsBusy changed to {IsBusy}. Search button should be {State}",
            value, value ? "disabled" : "enabled");
        SearchCommand.NotifyCanExecuteChanged();
        CopyResultsImagesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Opens the image in the system's default viewer (left-click on a tile).</summary>
    [RelayCommand]
    private void OpenImage(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(filePath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot open file {FilePath}: {Message}", filePath, ex.Message);
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
    /// Copies all result image files into <paramref name="destinationDirectory"/>.
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

            var sourcePaths = items.Select(i => i.FilePath).ToList();
            CopySummary summary = await Task.Run(() =>
                _copyService.CopyFilesAsync(sourcePaths, destinationDirectory, progress, ct), ct);

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
    private void CopyImagePath(string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            try
            {
                Clipboard.SetText(filePath);
                StatusMessage = $"Copied: {filePath}";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cannot copy to clipboard: {Message}", ex.Message);
                StatusMessage = $"Cannot copy to clipboard: {ex.Message}";
            }
        }
    }

    private void OnThumbnailPipelineStatus(string? text)
    {
        void Apply() => ThumbnailStatus = text ?? string.Empty;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Apply);
        else
            Apply();
    }
}