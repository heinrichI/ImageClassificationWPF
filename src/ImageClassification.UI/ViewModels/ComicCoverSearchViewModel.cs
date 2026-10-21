using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
using ImageClassification.UI.Configuration;
using ImageClassification.UI.Services;
using ImageClassification.UI.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ImageClassification.UI.ViewModels;

/// <summary>
/// Pure ViewModel for the Comic Cover Search tab.
/// No code-behind logic — all commands and bindings are here.
/// </summary>
public partial class ComicCoverSearchViewModel : ObservableObject, ITabMenuProvider, IThumbnailRequester
{
    private readonly IComicCoverSearchService _searchService;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<ComicCoverSearchViewModel> _logger;
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

    public ComicCoverSearchViewModel(IComicCoverSearchService searchService, IVectorStore vectorStore,
        ILogger<ComicCoverSearchViewModel> logger,
        ThumbnailProvider thumbnailProvider, ImageCopyService copyService, IUserSettingsStore settings)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _vectorStore = vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _thumbnailProvider = thumbnailProvider;
        _copyService = copyService ?? throw new ArgumentNullException(nameof(copyService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        // Live thumbnail pipeline status (which file is being read, queue, loaded total).
        // The provider is shared with the Search Image tab, so each tab subscribes its own
        // handler; the event can fire on a worker thread and is marshaled to the UI.
        _thumbnailProvider.StatusChanged += OnThumbnailPipelineStatus;

        SearchModes = new ObservableCollection<string>
        {
            "Covers only",
            "All pages"
        };

        // Restore the last used search mode (covers vs all pages) from the settings.
        SelectedModeIndex = Math.Clamp(settings.SearchMode, 0, SearchModes.Count - 1);

        // Restore the persisted search root directories.
        SearchDirectories = new ObservableCollection<string>(settings.SearchDirectories);

        // Restore the last used similarity threshold (the guard in OnSimilarityThresholdChanged
        // keeps this restore from rewriting the settings file).
        SimilarityThreshold = Math.Clamp(settings.SimilarityThreshold, 0d, 1d);

        // Restore the persisted phrase history (last 10 queries, newest first).
        PhraseHistory = new ObservableCollection<string>(settings.SearchPhraseHistory);

        Results.CollectionChanged += OnResultsCollectionChanged;
    }

    /// <summary>
    /// Requests (or bumps) the thumbnail of <paramref name="item"> — called by the item
    /// template's LoadThumbnailOnVisibleBehavior the moment the tile becomes visible. The
    /// provider keeps per-tile pipeline state, so a tile that is already in flight (or
    /// loaded) is NOT re-extracted: the request moves it to the front of whichever queue
    /// it sits in, making the on-screen tile load first. The search itself pre-queues only
    /// the first tiles up to the cache limit; the rest load on demand — exactly when their
    /// tile is scrolled into view.
    /// </summary>
    public void RequestThumbnail(ComicCoverItem item)
    {
        if (item is null) return;
        QueueThumbnail(item);
    }

    /// <summary>
    /// <see cref="IThumbnailRequester"/> entry point for the item template's visibility
    /// behavior: the tile's data context is cast to a cover item and queued (or bumped).
    /// </summary>
    public void RequestThumbnail(object? item)
    {
        if (item is ComicCoverItem coverItem)
            RequestThumbnail(coverItem);
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

    private void QueueThumbnail(ComicCoverItem item)
    {
        _thumbnailProvider.EnsurePageAndEnqueue(item.ArchivePath, item.PageIndex,
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
    partial void OnResultsChanged(ObservableCollection<ComicCoverItem>? oldValue, ObservableCollection<ComicCoverItem> newValue)
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
    /// doesn't re-select them on every launch). Every directory is scanned recursively for
    /// comic archives; search results from all roots are merged and de-duplicated.
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

    /// <summary>Full path of the archive the pipeline started processing most recently (empty when idle).</summary>
    [ObservableProperty]
    private string _currentFile = string.Empty;

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

    /// <summary>
    /// The user changed the covers/all-pages selection — persist it so a fresh process
    /// starts in the same mode.
    /// </summary>
    partial void OnSelectedModeIndexChanged(int value)
    {
        if (value == _settings.SearchMode)
            return;
        _settings.Save(_settings.OnnxThreadCount, _settings.GpuBatchSize, _settings.ThumbnailWidth,
            _settings.MaxCachedThumbnails, _settings.MaxPreviews, value);
    }

    /// <summary>
    /// The user moved the similarity threshold slider — persist it so a fresh process
    /// starts with the same result filter.
    /// </summary>
    partial void OnSimilarityThresholdChanged(double value)
    {
        if (Math.Abs(value - _settings.SimilarityThreshold) < 1e-9)
            return;
        _settings.SaveSimilarityThreshold(value);
    }

    /// <summary>
    /// A history phrase was picked from the dropdown — put it into the query box.
    /// The selection is kept (the dropdown mirrors the box); manual editing clears it
    /// via <see cref="OnQueryTextChanged"/>.
    /// </summary>
    partial void OnSelectedPhraseChanged(string? value)
    {
        if (value is not null)
            QueryText = value;
    }

    /// <summary>
    /// The query box no longer matches the selected history phrase (the user typed or
    /// edited) — clear the dropdown selection so it stops mirroring stale text.
    /// </summary>
    partial void OnQueryTextChanged(string value)
    {
        if (SelectedPhrase is not null && value != SelectedPhrase)
            SelectedPhrase = null;
    }

    /// <summary>
    /// Index of the inner panel inside the Comic Search tab: 0 = Search settings,
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
    //    properties on TaskbarItemInfo and are bound in MainWindow.xaml) ──

    [ObservableProperty]
    private double _taskbarProgressValue;

    [ObservableProperty]
    private TaskbarItemProgressState _taskbarProgressState = TaskbarItemProgressState.None;

    /// <summary>
    /// Mirrors search progress on the Windows taskbar button: determinate bar while the archive
    /// total is known, animated stripes while it is not (model download / directory scan),
    /// hidden when idle. Called on every status update (already throttled) and on start/stop.
    /// Note: TaskbarItemInfo.ProgressValue is coerced by WPF to the range 0.0–1.0.
    /// </summary>
    private void UpdateTaskbar()
    {
        TaskbarProgressState = !IsBusy
            ? TaskbarItemProgressState.None
            : ProgressTotal > 0
                ? TaskbarItemProgressState.Normal
                : TaskbarItemProgressState.Indeterminate;

        TaskbarProgressValue = IsBusy && ProgressTotal > 0
            ? ProgressCurrent / (double)ProgressTotal   // 0.0–1.0, coerced by WPF
            : 0;
    }

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

    [ObservableProperty]
    private ObservableCollection<ComicCoverItem> _results = new();

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
    /// Archive diagnostics collected during the current search (container mismatches such as
    /// "ZIP inside .cbr", bulk-pass fallbacks). Accumulated in a visible UI list; cleared
    /// on search start. Capped so a pathological library cannot grow it unboundedly.
    /// </summary>
    public ObservableCollection<string> ArchiveNotes { get; } = new();

    // Guards the notes collection: in the app every mutation is dispatcher-marshalled,
    // but under tests (no WPF dispatcher) status updates can apply concurrently.
    private readonly object _notesLock = new();

    private const int MaxArchiveNotes = 500;

    /// <summary>Item count of <see cref="ArchiveNotes"/> (drives the notes panel visibility).</summary>
    public int ArchiveNotesCount { get; private set; }

    public ObservableCollection<string> SearchModes { get; }

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


    /// <summary>
    /// Returns the current SearchMode derived from SelectedModeIndex.
    /// </summary>
    private SearchMode CurrentMode => (SearchMode)SelectedModeIndex;

    // ────────────────────────────── Commands ──────────────────────────────

    [RelayCommand]
    private void AddDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select directory with comic archives (CBZ/CBR/PDF)"
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

    private void PersistDirectories() => _settings.SaveSearchDirectories(SearchDirectories.ToList());

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

        _settings.SaveSearchPhraseHistory(updated);

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
        _logger.LogInformation("SearchAsync started. ModeIndex={ModeIndex}, Directories='{Directories}', QueryLength={QueryLength}",
            SelectedModeIndex, string.Join("; ", SearchDirectories), QueryText?.Length ?? 0);

        // Validate inputs
        if (SearchDirectories.Count == 0)
        {
            StatusMessage = "Please add a search directory first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(QueryText))
        {
            StatusMessage = "Please enter a search query (e.g. 'beach', 'summer').";
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
            ArchiveNotes.Clear();
            ArchiveNotesCount = 0;
        }
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

                // Archive diagnostics (rare per-search events) — accumulate in the notes list.
                if (s.Note is not null)
                {
                    lock (_notesLock)
                    {
                        ArchiveNotes.Add(s.Note);
                        if (ArchiveNotes.Count > MaxArchiveNotes)
                            ArchiveNotes.RemoveAt(0);
                    }
                    ArchiveNotesCount = ArchiveNotes.Count;
                    OnPropertyChanged(nameof(ArchiveNotesCount));
                }

                // "Currently processing" file label — applied on every update (unthrottled
                // in the VM; the service already rate-limits these reports).
                if (s.CurrentFile is not null)
                    CurrentFile = s.CurrentFile;

                // Refresh the taskbar button on every status event (cheap: two property writes)
                UpdateTaskbar();

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

            // No cache clearing on mode change: covers and all-pages keys are
            // disjoint ("{path}" vs "{path}|page=N"), and entry freshness is already
            // validated via LastWriteTimeUtc/Size in the vector store. (The previous
            // auto-clear here wiped the whole cache after every app restart in
            // AllPages mode, forcing full re-encoding on each launch.)

            // Run on thread-pool so the UI thread is never blocked by IO or CPU work
            // (ScanArchivesRecursive, text-encoder init, GPU inference all happen off UI).
            // Progress<T> was constructed on UI thread and already marshals via Dispatcher.
            IProgress<ComicSearchStatus> outerStatus = status; // interface view: Report is on IProgress
            List<ComicCoverResult> results = await Task.Run(async () =>
            {
                var merged = new List<ComicCoverResult>();
                var roots = SearchDirectories.ToList();
                for (var i = 0; i < roots.Count; i++)
                {
                    var dir = roots[i];

                    // Per-root phase marker: "Processing archives: <dir> (2/3)". During the
                    // root the service's per-archive detail lines carry the directory prefix.
                    outerStatus.Report(new ComicSearchStatus
                    {
                        Phase = $"Processing archives: {dir} ({i + 1}/{roots.Count})"
                    });

                    if (!System.IO.Directory.Exists(dir))
                    {
                        outerStatus.Report(new ComicSearchStatus { Note = $"Directory not found, skipped: {dir}" });
                        continue;
                    }

                    var rootStatus = new Progress<ComicSearchStatus>(s =>
                        outerStatus.Report(string.IsNullOrEmpty(s.Detail) ? s : s with { Detail = $"{dir} | {s.Detail}" }));

                    if (currentMode == SearchMode.AllPages)
                        merged.AddRange(await _searchService.SearchAllPagesAsync(dir, QueryText, rootStatus, ct).ConfigureAwait(false));
                    else
                        merged.AddRange(await _searchService.SearchAsync(dir, QueryText, rootStatus, ct).ConfigureAwait(false));
                }

                // Nested/overlapping roots can surface the same archive twice — de-duplicate
                // by (archive, page) keeping the first occurrence, then rank globally.
                return merged
                    .GroupBy(r => (r.ArchivePath, r.PageIndex))
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
            // Build items outside the observable and assign the collection in one go below:
            // a single CollectionChanged(Reset) instead of N per-item Adds, each of which
            // used to run the VM's CollectionChanged handler (command refresh + 2x property
            // notifications + tab-header re-render) on the UI thread.
            // Every item starts with a same-size placeholder so later real-preview swaps
            // re-render the cell only — no results-panel re-layout per preview.
            var swPopulate = Stopwatch.StartNew();
            var items = new List<ComicCoverItem>(filtered.Count);
            for (var i = 0; i < filtered.Count; i++)
            {
                var result = filtered[i];
                var item = new ComicCoverItem
                {
                    ArchivePath = result.ArchivePath,
                    ArchiveFileName = result.ArchiveFileName,
                    SimilarityScore = result.SimilarityScore,
                    PageIndex = result.PageIndex,
                    PageCount = result.PageCount,
                    // keeps the image cell size stable until the preview is decoded; the
                    // real thumbnail is swapped in when the tile becomes visible
                    Thumbnail = _thumbnailProvider.GetPlaceholder()
                };

                items.Add(item);
            }

            // One bulk assignment → one CollectionChanged(Reset)
            Results = new ObservableCollection<ComicCoverItem>(items);

            // Background pre-fetch of the first tiles (up to the cache limit; see the
            // note above the populate block).
            var prefetchCount = Math.Min(items.Count, _settings.MaxCachedThumbnails);
            for (var i = prefetchCount - 1; i >= 0; i--)
            {
                QueueThumbnail(items[i]);
            }

            _logger.LogWarning(
                "[Perf] Results populated: {Shown} item(s) (filtered from {Total}), in {Ms} ms on UI thread",
                items.Count, results.Count, swPopulate.ElapsedMilliseconds);

            if (Results.Count > 0)
            {
                var modeText = currentMode == SearchMode.AllPages ? "pages" : "covers";
                StatusMessage = $"Found {Results.Count} matching {modeText} (filtered from {results.Count} total, " +
                    $"scores: {filtered.Min(r => r.SimilarityScore):P1}–{filtered.Max(r => r.SimilarityScore):P1}, " +
                    $"median: {filtered.OrderBy(r => r.SimilarityScore).ElementAt(filtered.Count / 2).SimilarityScore:P1})";
                SelectedInnerTabIndex = 1; // auto-switch to the full-height results panel
                // Measure how long the UI thread is blocked by the results-panel
                // materialization (container generation + WrapPanel layout + render) after
                // the switch — "time until responsive again" for this result size.
                _ = MeasureUiLatencyAsync();
            }
            else if (results.Count == 0)
            {
                // No results at all — keep the service's final phase if it explains why
                // (e.g. "No comic archives found in the selected directory"); when every
                // root was skipped (missing directory notes) the phase line is misleading.
                StatusMessage = ArchiveNotes.Count > 0
                    ? "No archives were processed — see notes."
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

    /// <summary>
    /// Perf probe: waits until the dispatcher can process a Background-priority (i.e.
    /// post-layout/post-render) work item again and logs how long the UI thread was busy.
    /// A large number right after the results switch means the one-shot materialization of
    /// all result containers (non-virtualized WrapPanel) is the freeze source.
    /// </summary>
    private async Task MeasureUiLatencyAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        var sw = Stopwatch.StartNew();
        try
        {
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            _logger.LogWarning(
                "[Perf] UI thread busy for {Ms} ms after results switch (results={Count}) " +
                "— layout/input/render were blocked until now",
                sw.ElapsedMilliseconds, Results.Count);
        }
        catch (Exception) { /* app shutting down */ }
    }

    // ─────────────────────── Vector cache menu commands ───────────────────────

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

    private string _infoText = ImageClassification.UI.Services.TileInfoText.Fallback;
    /// <summary>
    /// Line under the tile: original resolution and size of the displayed page
    /// ("1920 × 1080 · 1.4 MB"); the click hint until the thumbnail pipeline reports it.
    /// </summary>
    public string InfoText
    {
        get => _infoText;
        set => SetProperty(ref _infoText, value);
    }
}