using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
using ImageClassification.UI.Configuration;
using ImageClassification.UI.Services;

namespace ImageClassification.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IModelDownloader _downloader;
    private readonly IUserSettingsStore _store;

    // Last committed (valid) values — used to roll back out-of-range input.
    private int _lastThreadCount;
    private int _lastGpuBatch;
    private int _lastWidth;
    private int _lastMaxCached;
    private int _lastMaxPreviews;

    public SettingsViewModel(
        IModelDownloader downloader,
        IUserSettingsStore store)
    {
        _downloader = downloader;
        _store = store;

        _onnxThreadCount = _lastThreadCount = store.OnnxThreadCount;
        _gpuBatchSize = _lastGpuBatch = store.GpuBatchSize;
        _thumbnailWidth = _lastWidth = store.ThumbnailWidth;
        _maxCachedThumbnails = _lastMaxCached = store.MaxCachedThumbnails;
        _maxPreviews = _lastMaxPreviews = store.MaxPreviews;

        _cudaPath = store.CudaPath;
        _cudnnPath = store.CudnnPath;
        RefreshGpuStatus();

        RefreshModels();
    }

    /// <summary>
    /// ONNX Runtime thread count (intra-op and inter-op). 0 = ONNX Runtime default.
    /// Applied when an ONNX session is created, i.e. on model load/reload.
    /// </summary>
    [ObservableProperty] private int _onnxThreadCount;

    /// <summary>
    /// Images per GPU (ONNX) inference batch. Larger = faster but more VRAM per call.
    /// Applied from the next search — no restart needed.
    /// </summary>
    [ObservableProperty] private int _gpuBatchSize;

    [ObservableProperty] private int _thumbnailWidth;
    [ObservableProperty] private int _maxCachedThumbnails;

    /// <summary>
    /// Maximum number of result previews (thumbnails) to load: only the top N results
    /// get a real preview, the rest show placeholders (all results are still listed).
    /// 0 = no limit. Applied from the next search.
    /// </summary>
    [ObservableProperty] private int _maxPreviews;

    /// <summary>
    /// CUDA bin directory (folder with <c>cudart64_*.dll</c>) prepended to the process
    /// PATH at startup. Empty = auto-detected on the first launch. Applied after restart.
    /// </summary>
    [ObservableProperty] private string _cudaPath;

    /// <summary>
    /// cuDNN bin directory (folder with <c>cudnn*.dll</c>), same semantics as <see cref="CudaPath"/>.
    /// </summary>
    [ObservableProperty] private string _cudnnPath;

    /// <summary>Current state of the GPU paths (found / not found / not set).</summary>
    [ObservableProperty] private string _gpuStatusText;

    public ObservableCollection<ModelDownloadInfo> AvailableModels { get; } = new();

    partial void OnOnnxThreadCountChanged(int value)
    {
        if (value < 0 || value > 256)
        {
            _onnxThreadCount = _lastThreadCount;
            return;
        }

        _lastThreadCount = value;
        _store.Save(value, _gpuBatchSize, _thumbnailWidth, _maxCachedThumbnails, _maxPreviews, _store.SearchMode);
    }

    partial void OnGpuBatchSizeChanged(int value)
    {
        if (value < 1 || value > 256)
        {
            _gpuBatchSize = _lastGpuBatch;
            return;
        }

        _lastGpuBatch = value;
        _store.Save(_onnxThreadCount, value, _thumbnailWidth, _maxCachedThumbnails, _maxPreviews, _store.SearchMode);
    }

    partial void OnThumbnailWidthChanged(int value)
    {
        if (value < 32 || value > 2048)
        {
            _thumbnailWidth = _lastWidth;
            return;
        }

        _lastWidth = value;
        _store.Save(_onnxThreadCount, _gpuBatchSize, value, _maxCachedThumbnails, _maxPreviews, _store.SearchMode);
    }

    partial void OnMaxCachedThumbnailsChanged(int value)
    {
        if (value < 16 || value > 100_000)
        {
            _maxCachedThumbnails = _lastMaxCached;
            return;
        }

        _lastMaxCached = value;
        _store.Save(_onnxThreadCount, _gpuBatchSize, _thumbnailWidth, value, _maxPreviews, _store.SearchMode);
    }

    partial void OnMaxPreviewsChanged(int value)
    {
        if (value < 0 || value > 100_000)
        {
            _maxPreviews = _lastMaxPreviews;
            return;
        }

        _lastMaxPreviews = value;
        _store.Save(_onnxThreadCount, _gpuBatchSize, _thumbnailWidth, _maxCachedThumbnails, value, _store.SearchMode);
    }

    [RelayCommand]
    private void RefreshModels()
    {
        AvailableModels.Clear();
        foreach (var model in _downloader.GetAvailableModels())
        {
            AvailableModels.Add(model);
        }
    }

    /// <summary>Picks a folder for the CUDA bin field.</summary>
    [RelayCommand]
    private void BrowseCudaPath()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the CUDA bin directory (folder with cudart64_*.dll)"
        };
        if (dialog.ShowDialog() == true)
        {
            CudaPath = dialog.FolderName;
            RefreshGpuStatus();
        }
    }

    /// <summary>Picks a folder for the cuDNN bin field.</summary>
    [RelayCommand]
    private void BrowseCudnnPath()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the cuDNN bin directory (folder with cudnn*.dll)"
        };
        if (dialog.ShowDialog() == true)
        {
            CudnnPath = dialog.FolderName;
            RefreshGpuStatus();
        }
    }

    /// <summary>
    /// Auto-detects the still-empty paths (registry / Program Files synchronously-safe
    /// part, then the deep per-drive cuDNN scan in the background), fills the fields,
    /// saves to user-settings.json and refreshes the status. Manually set fields are
    /// never overwritten.
    /// </summary>
    [RelayCommand]
    private async Task DetectGpuPathsAsync()
    {
        GpuStatusText = "Detecting CUDA/cuDNN…";

        var detectCuda = string.IsNullOrWhiteSpace(CudaPath);
        var detectCudnn = string.IsNullOrWhiteSpace(CudnnPath);

        if (detectCuda)
            CudaPath = await Task.Run(() => GpuLibraryDetector.DetectCudaPath()) ?? string.Empty;
        if (detectCudnn)
            CudnnPath = await Task.Run(() => GpuLibraryDetector.DetectCudnnPathFull()) ?? string.Empty;

        _store.SaveGpuPaths(CudaPath, CudnnPath);
        RefreshGpuStatus(saved: true);
    }

    private void RefreshGpuStatus(bool saved = false)
    {
        var lines = new List<string>();
        if (saved)
            lines.Add("Saved to user-settings.json.");
        lines.Add(DescribeGpuPath("CUDA", CudaPath, GpuLibraryDetector.HasCudart));
        lines.Add(DescribeGpuPath("cuDNN", CudnnPath, GpuLibraryDetector.ContainsCudnn));
        lines.Add("The paths take effect after the app is restarted.");
        GpuStatusText = string.Join(Environment.NewLine, lines);
    }

    private static string DescribeGpuPath(string label, string path, Func<string, bool> marker)
    {
        if (string.IsNullOrWhiteSpace(path))
            return $"{label}: (not set — use Detect & Save or Browse)";
        return marker(path)
            ? $"{label}: {path}"
            : $"{label}: {path} — folder not found or missing the marker DLLs";
    }

    [RelayCommand]
    private async Task DownloadModelAsync(ModelDownloadInfo? model)
    {
        if (model == null || model.IsDownloaded) return;

        var dialog = new Views.DownloadProgressDialog(
            $"Downloading {model.Name}...",
            async (progress, cancellationToken) =>
            {
                await _downloader.DownloadModelAsync(model, progress, cancellationToken);
            });

        dialog.Owner = Application.Current.MainWindow;
        dialog.ShowDialog();

        RefreshModels();
    }

}