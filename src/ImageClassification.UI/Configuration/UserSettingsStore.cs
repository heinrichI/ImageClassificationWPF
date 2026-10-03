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

    /// <summary>Path of the persisted settings file.</summary>
    string File { get; }

    /// <summary>
    /// Persists the values and updates the in-memory state.
    /// Fails soft (logs a warning) if the program directory is read-only.
    /// </summary>
    void Save(int onnxThreadCount, int gpuBatchSize, int thumbnailWidth, int maxCachedThumbnails);
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

    public int OnnxThreadCount => _onnxThreadCount;
    public int GpuBatchSize => _gpuBatchSize;
    public int ThumbnailWidth => _thumbnailWidth;
    public int ThumbnailHeight => _thumbnailHeight;
    public int MaxCachedThumbnails => _maxCachedThumbnails;

    public UserSettingsStore(ILogger<UserSettingsStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        File = Path.Combine(AppContext.BaseDirectory, "user-settings.json");

        // Load persisted values if the file exists (fresh install → defaults).
        Load();

        // Ensure the file exists so the watcher has something to observe.
        if (!System.IO.File.Exists(File))
            Save(4, 32, 180, 400);

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

    public void Save(int onnxThreadCount, int gpuBatchSize, int thumbnailWidth, int maxCachedThumbnails)
    {
        lock (_lock)
        {
            _onnxThreadCount = onnxThreadCount;
            _gpuBatchSize = gpuBatchSize;
            _thumbnailWidth = thumbnailWidth;
            _maxCachedThumbnails = maxCachedThumbnails;
        }

        var payload = new
        {
            OnnxThreadCount = onnxThreadCount,
            GpuBatchSize = gpuBatchSize,
            ThumbnailWidth = thumbnailWidth,
            MaxCachedThumbnails = maxCachedThumbnails
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