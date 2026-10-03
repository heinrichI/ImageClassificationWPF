using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
using ImageClassification.UI.Configuration;

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

    public ObservableCollection<ModelDownloadInfo> AvailableModels { get; } = new();

    partial void OnOnnxThreadCountChanged(int value)
    {
        if (value < 0 || value > 256)
        {
            _onnxThreadCount = _lastThreadCount;
            return;
        }

        _lastThreadCount = value;
        _store.Save(value, _gpuBatchSize, _thumbnailWidth, _maxCachedThumbnails);
    }

    partial void OnGpuBatchSizeChanged(int value)
    {
        if (value < 1 || value > 256)
        {
            _gpuBatchSize = _lastGpuBatch;
            return;
        }

        _lastGpuBatch = value;
        _store.Save(_onnxThreadCount, value, _thumbnailWidth, _maxCachedThumbnails);
    }

    partial void OnThumbnailWidthChanged(int value)
    {
        if (value < 32 || value > 2048)
        {
            _thumbnailWidth = _lastWidth;
            return;
        }

        _lastWidth = value;
        _store.Save(_onnxThreadCount, _gpuBatchSize, value, _maxCachedThumbnails);
    }

    partial void OnMaxCachedThumbnailsChanged(int value)
    {
        if (value < 16 || value > 100_000)
        {
            _maxCachedThumbnails = _lastMaxCached;
            return;
        }

        _lastMaxCached = value;
        _store.Save(_onnxThreadCount, _gpuBatchSize, _thumbnailWidth, value);
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