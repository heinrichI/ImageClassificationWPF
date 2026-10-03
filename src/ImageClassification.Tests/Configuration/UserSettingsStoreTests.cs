using System.IO;
using ImageClassification.UI.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ImageClassification.Tests;

/// <summary>
/// Exercises <see cref="UserSettingsStore"/> against the real (test-output) program
/// directory: file creation, save persistence, live value updates and the
/// file-watcher path for external edits. All tests share one class so they run
/// sequentially and don't race on the same file/watcher.
/// </summary>
public class UserSettingsStoreTests : IDisposable
{
    private static string SettingsFile =>
        Path.Combine(AppContext.BaseDirectory, "user-settings.json");

    private UserSettingsStore? _store;

    public void Dispose()
    {
        _store?.Dispose();
        _store = null;
    }

    [Fact]
    public void Constructor_CreatesFile_WithDefaults()
    {
        if (File.Exists(SettingsFile))
            File.Delete(SettingsFile);

        _store = CreateStore();
        var store = _store;

        Assert.True(File.Exists(SettingsFile));
        Assert.Equal(4, store.OnnxThreadCount);
        Assert.Equal(32, store.GpuBatchSize);
        Assert.Equal(180, store.ThumbnailWidth);
        Assert.Equal(240, store.ThumbnailHeight);
        Assert.Equal(400, store.MaxCachedThumbnails);

        var content = File.ReadAllText(SettingsFile);
        Assert.Contains("\"OnnxThreadCount\": 4", content);
        Assert.Contains("\"GpuBatchSize\": 32", content);
        Assert.Contains("\"ThumbnailWidth\": 180", content);
        Assert.Contains("\"MaxCachedThumbnails\": 400", content);
    }

    [Fact]
    public void Save_PersistsValues_AndUpdatesLiveValues()
    {
        var store = CreateStore();
        _store = store;

        store.Save(7, 64, 300, 500);

        Assert.Equal(7, store.OnnxThreadCount);
        Assert.Equal(64, store.GpuBatchSize);
        Assert.Equal(300, store.ThumbnailWidth);
        Assert.Equal(500, store.MaxCachedThumbnails);

        var content = File.ReadAllText(SettingsFile);
        Assert.Contains("\"OnnxThreadCount\": 7", content);
        Assert.Contains("\"GpuBatchSize\": 64", content);
        Assert.Contains("\"ThumbnailWidth\": 300", content);
        Assert.Contains("\"MaxCachedThumbnails\": 500", content);
    }

    [Fact]
    public void ExternalEdit_IsPickedUp_ByWatcher()
    {
        var store = CreateStore();
        _store = store;

        store.Save(1, 8, 2, 3);

        // Simulate the user editing the file by hand
        File.WriteAllText(SettingsFile,
            "{\n  \"OnnxThreadCount\": 99,\n  \"GpuBatchSize\": 44,\n  \"ThumbnailWidth\": 222,\n  \"MaxCachedThumbnails\": 333\n}\n");

        // The watcher debounces ~100 ms; give it up to 5 seconds.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (store.GpuBatchSize != 44 && DateTime.UtcNow < deadline)
            Thread.Sleep(100);

        Assert.Equal(99, store.OnnxThreadCount);
        Assert.Equal(44, store.GpuBatchSize);
        Assert.Equal(222, store.ThumbnailWidth);
        Assert.Equal(333, store.MaxCachedThumbnails);
    }

    [Fact]
    public void CorruptFile_KeepsCurrentValues()
    {
        var store = CreateStore();
        _store = store;
        store.Save(5, 6, 7, 8);

        File.WriteAllText(SettingsFile, "{ not valid json");

        // Give the watcher time to fire; the load must fail soft and keep values.
        Thread.Sleep(500);

        Assert.Equal(5, store.OnnxThreadCount);
        Assert.Equal(6, store.GpuBatchSize);
        Assert.Equal(7, store.ThumbnailWidth);
        Assert.Equal(8, store.MaxCachedThumbnails);
    }

    private static UserSettingsStore CreateStore() =>
        new(NullLogger<UserSettingsStore>.Instance);
}