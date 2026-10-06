using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ImageClassification.UI.Configuration;

/// <summary>
/// Live view over user settings, persisted to <c>user-settings.json</c> in the program
/// directory. Values update without a restart: saves through <see cref="Save"/> apply
/// immediately, and external edits to the file are picked up by a file watcher.
/// Consumers (ONNX encoders via <c>IOnnxRuntimeOptions</c>, the thumbnail pipeline,
/// the settings view-model) read the properties on every use.
/// </summary>
public interface IUserSettingsStore
{
    /// <summary>ONNX Runtime thread count (0 = ONNX Runtime default).</summary>
    int OnnxThreadCount { get; }

    /// <summary>Images per GPU (ONNX) inference batch.</summary>
    int GpuBatchSize { get; }

    /// <summary>Default thumbnail width in pixels.</summary>
    int ThumbnailWidth { get; }

    /// <summary>Default thumbnail height in pixels.</summary>
    int ThumbnailHeight { get; }

    /// <summary>Maximum number of thumbnails kept in the cache.</summary>
    int MaxCachedThumbnails { get; }

    /// <summary>
    /// Maximum number of result previews (thumbnails) to load: only the top N best
    /// results get a real preview, the rest show placeholders (all results are still
    /// listed — the grid is virtualized). 0 = load a preview for every result.
    /// Applied from the next search.
    /// </summary>
    int MaxPreviews { get; }

    /// <summary>
    /// Last comic-search mode: 0 = covers only, 1 = all pages (ComicCoverSearchViewModel
    /// SelectedModeIndex). Persisted so a fresh process starts in the same mode.
    /// </summary>
    int SearchMode { get; }

    /// <summary>
    /// Search root directories for the comic search (persisted; a fresh process
    /// restores the list so the user doesn't re-select them on every launch).
    /// </summary>
    IReadOnlyList<string> SearchDirectories { get; }

    /// <summary>Path of the persisted settings file.</summary>
    string File { get; }

    /// <summary>
    /// Persists the values and updates the in-memory state.
    /// Fails soft (logs a warning) if the program directory is read-only.
    /// </summary>
    void Save(int onnxThreadCount, int gpuBatchSize, int thumbnailWidth, int maxCachedThumbnails, int maxPreviews, int searchMode);

    /// <summary>Persists the search root directories (keeps the other values untouched).</summary>
    void SaveSearchDirectories(IReadOnlyList<string> directories);

    /// <summary>
    /// History of the last comic-search queries (newest first, max 10). Persisted so the
    /// dropdown survives a restart.
    /// </summary>
    IReadOnlyList<string> SearchPhraseHistory { get; }

    /// <summary>Persists the search-phrase history (keeps the other values untouched).</summary>
    void SaveSearchPhraseHistory(IReadOnlyList<string> history);

    /// <summary>
    /// Minimum similarity score for the comic search results (0..1). Persisted so a
    /// fresh process starts with the same filter.
    /// </summary>
    double SimilarityThreshold { get; }

    /// <summary>Persists the similarity threshold (keeps the other values untouched).</summary>
    void SaveSimilarityThreshold(double value);

    /// <summary>
    /// Search root directories for the plain-image search (persisted; a fresh process
    /// restores the list so the user doesn't re-select them on every launch).
    /// </summary>
    IReadOnlyList<string> ImageSearchDirectories { get; }

    /// <summary>Persists the image-search root directories (keeps the other values untouched).</summary>
    void SaveImageSearchDirectories(IReadOnlyList<string> directories);

    /// <summary>
    /// History of the last image-search queries (newest first, max 10). Persisted so the
    /// dropdown survives a restart.
    /// </summary>
    IReadOnlyList<string> ImageSearchPhraseHistory { get; }

    /// <summary>Persists the image-search phrase history (keeps the other values untouched).</summary>
    void SaveImageSearchPhraseHistory(IReadOnlyList<string> history);

    /// <summary>
    /// Minimum similarity score for the image search results (0..1). Persisted so a fresh
    /// process starts with the same filter.
    /// </summary>
    double ImageSearchSimilarityThreshold { get; }

    /// <summary>Persists the image-search similarity threshold (keeps the other values untouched).</summary>
    void SaveImageSearchSimilarityThreshold(double value);

    /// <summary>
    /// "Recurse subfolders" option of the image search (default: true). Persisted so a
    /// fresh process starts with the same scan scope.
    /// </summary>
    bool ImageSearchRecurseSubfolders { get; }

    /// <summary>Persists the image-search recursion option (keeps the other values untouched).</summary>
    void SaveImageSearchRecurseSubfolders(bool value);
}

internal sealed class UserSettingsStore : IUserSettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<UserSettingsStore> _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly FileSystemWatcher _watcher;

    public string File { get; }

    private volatile int _onnxThreadCount = 4;
    private volatile int _gpuBatchSize = 32;
    private volatile int _thumbnailWidth = 180;
    private volatile int _thumbnailHeight = 240;
    private volatile int _maxCachedThumbnails = 400;
    private volatile int _maxPreviews = 500;
    private volatile int _searchMode = 0;
    private volatile IReadOnlyList<string> _searchDirectories = Array.Empty<string>();
    private volatile IReadOnlyList<string> _searchPhraseHistory = Array.Empty<string>();
    private double _similarityThreshold = 0.25; // lock-guarded; volatile is not allowed on double
    private volatile IReadOnlyList<string> _imageSearchDirectories = Array.Empty<string>();
    private volatile IReadOnlyList<string> _imageSearchPhraseHistory = Array.Empty<string>();
    private double _imageSearchSimilarityThreshold = 0.25; // lock-guarded
    private volatile bool _imageSearchRecurseSubfolders = true;

    public int OnnxThreadCount => _onnxThreadCount;
    public int GpuBatchSize => _gpuBatchSize;
    public int ThumbnailWidth => _thumbnailWidth;
    public int ThumbnailHeight => _thumbnailHeight;
    public int MaxCachedThumbnails => _maxCachedThumbnails;
    public int MaxPreviews => _maxPreviews;
    public int SearchMode => _searchMode;
    public IReadOnlyList<string> SearchDirectories => _searchDirectories;
    public IReadOnlyList<string> SearchPhraseHistory => _searchPhraseHistory;
    public double SimilarityThreshold => _similarityThreshold;
    public IReadOnlyList<string> ImageSearchDirectories => _imageSearchDirectories;
    public IReadOnlyList<string> ImageSearchPhraseHistory => _imageSearchPhraseHistory;
    public double ImageSearchSimilarityThreshold => _imageSearchSimilarityThreshold;
    public bool ImageSearchRecurseSubfolders => _imageSearchRecurseSubfolders;

    public UserSettingsStore(ILogger<UserSettingsStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        File = Path.Combine(AppContext.BaseDirectory, "user-settings.json");

        // Load persisted values if the file exists (fresh install → defaults).
        Load();

        // Ensure the file exists so the watcher has something to observe.
        if (!System.IO.File.Exists(File))
            Save(4, 32, 180, 400, 500, 0);

        // Watch for external edits (e.g. the user tweaking the JSON by hand).
        var dir = Path.GetDirectoryName(File)!;
        _watcher = new FileSystemWatcher(dir)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = false
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += (s, e) => OnFileChanged(s, new FileSystemEventArgs(WatcherChangeTypes.Renamed, _watcher.Path, e.Name));
        _watcher.EnableRaisingEvents = true;
    }

    public void Save(int onnxThreadCount, int gpuBatchSize, int thumbnailWidth, int maxCachedThumbnails, int maxPreviews, int searchMode)
    {
        lock (_lock)
        {
            _onnxThreadCount = onnxThreadCount;
            _gpuBatchSize = gpuBatchSize;
            _thumbnailWidth = thumbnailWidth;
            _maxCachedThumbnails = maxCachedThumbnails;
            _maxPreviews = maxPreviews;
            _searchMode = searchMode;
        }

        Persist();
    }

    public void SaveSearchDirectories(IReadOnlyList<string> directories)
    {
        lock (_lock)
        {
            _searchDirectories = directories?.ToList() ?? new List<string>();
        }

        Persist();
    }

    public void SaveSearchPhraseHistory(IReadOnlyList<string> history)
    {
        lock (_lock)
        {
            _searchPhraseHistory = history?.ToList() ?? new List<string>();
        }

        Persist();
    }

    public void SaveSimilarityThreshold(double value)
    {
        lock (_lock)
        {
            _similarityThreshold = value;
        }

        Persist();
    }

    public void SaveImageSearchDirectories(IReadOnlyList<string> directories)
    {
        lock (_lock)
        {
            _imageSearchDirectories = directories?.ToList() ?? new List<string>();
        }

        Persist();
    }

    public void SaveImageSearchPhraseHistory(IReadOnlyList<string> history)
    {
        lock (_lock)
        {
            _imageSearchPhraseHistory = history?.ToList() ?? new List<string>();
        }

        Persist();
    }

    public void SaveImageSearchSimilarityThreshold(double value)
    {
        lock (_lock)
        {
            _imageSearchSimilarityThreshold = value;
        }

        Persist();
    }

    public void SaveImageSearchRecurseSubfolders(bool value)
    {
        lock (_lock)
        {
            _imageSearchRecurseSubfolders = value;
        }

        Persist();
    }

    /// <summary>Writes every current in-memory value to the settings file (fail-soft).</summary>
    private void Persist()
    {
        lock (_lock)
        {
            var payload = new
            {
                OnnxThreadCount = _onnxThreadCount,
                GpuBatchSize = _gpuBatchSize,
                ThumbnailWidth = _thumbnailWidth,
                MaxCachedThumbnails = _maxCachedThumbnails,
                MaxPreviews = _maxPreviews,
                SearchMode = _searchMode,
                SearchDirectories = _searchDirectories,
                SearchPhraseHistory = _searchPhraseHistory,
                SimilarityThreshold = _similarityThreshold,
                ImageSearchDirectories = _imageSearchDirectories,
                ImageSearchPhraseHistory = _imageSearchPhraseHistory,
                ImageSearchSimilarityThreshold = _imageSearchSimilarityThreshold,
                ImageSearchRecurseSubfolders = _imageSearchRecurseSubfolders
            };

            try
            {
                _writeLock.Wait();
                try
                {
                    var dir = Path.GetDirectoryName(File)!;
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var tmp = File + ".tmp";
                    System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOptions));
                    System.IO.File.Move(tmp, File, overwrite: true);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (Exception ex)
            {
                // The program directory may be read-only (e.g. installed under Program Files):
                // values still apply in-memory for the current run.
                _logger.LogWarning(ex, "UserSettingsStore: cannot save {File}; values apply for this run only", File);
            }
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!string.Equals(e.Name, Path.GetFileName(File), StringComparison.OrdinalIgnoreCase))
            return;

        // Debounce: a save can fire several watcher events in quick succession.
        _ = Task.Delay(100).ContinueWith(_ => Load());
    }

    private void Load()
    {
        try
        {
            if (!System.IO.File.Exists(File))
                return;

            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(File));
            var root = doc.RootElement;

            int ReadInt(string name, int fallback)
                => root.TryGetProperty(name, out var el) && el.TryGetInt32(out var v) ? v : fallback;

            lock (_lock)
            {
                _onnxThreadCount = ReadInt("OnnxThreadCount", _onnxThreadCount);
                _gpuBatchSize = ReadInt("GpuBatchSize", _gpuBatchSize);
                _thumbnailWidth = ReadInt("ThumbnailWidth", _thumbnailWidth);
                _maxCachedThumbnails = ReadInt("MaxCachedThumbnails", _maxCachedThumbnails);
                // "MaxDisplayedResults" is the legacy key from before the MaxPreviews
                // rename — old settings files keep working.
                _maxPreviews = ReadInt("MaxPreviews", ReadInt("MaxDisplayedResults", _maxPreviews));
                _searchMode = ReadInt("SearchMode", _searchMode);

                if (root.TryGetProperty("SimilarityThreshold", out var stEl) && stEl.TryGetDouble(out var stv))
                    _similarityThreshold = stv;

                // Old files have no key — the in-memory default (empty list) is kept.
                if (root.TryGetProperty("SearchDirectories", out var dirsEl) && dirsEl.ValueKind == JsonValueKind.Array)
                {
                    _searchDirectories = dirsEl.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToList();
                }

                if (root.TryGetProperty("SearchPhraseHistory", out var histEl) && histEl.ValueKind == JsonValueKind.Array)
                {
                    _searchPhraseHistory = histEl.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToList();
                }

                if (root.TryGetProperty("ImageSearchSimilarityThreshold", out var istsEl) && istsEl.TryGetDouble(out var istv))
                    _imageSearchSimilarityThreshold = istv;

                if (root.TryGetProperty("ImageSearchRecurseSubfolders", out var irEl))
                    _imageSearchRecurseSubfolders = irEl.ValueKind == JsonValueKind.True;

                if (root.TryGetProperty("ImageSearchDirectories", out var idirsEl) && idirsEl.ValueKind == JsonValueKind.Array)
                {
                    _imageSearchDirectories = idirsEl.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToList();
                }

                if (root.TryGetProperty("ImageSearchPhraseHistory", out var ihistEl) && ihistEl.ValueKind == JsonValueKind.Array)
                {
                    _imageSearchPhraseHistory = ihistEl.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToList();
                }
            }
        }
        catch
        {
            // Corrupt or in-flight write — keep the current values.
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _writeLock.Dispose();
    }
}