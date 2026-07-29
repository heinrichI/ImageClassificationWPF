using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;

namespace ImageClassification.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IModelDownloader _downloader;
    private CancellationTokenSource? _cts;

    public SettingsViewModel(IModelDownloader downloader)
    {
        _downloader = downloader;
        RefreshModels();
    }

    [ObservableProperty] private int _onnxThreadCount = 4;
    [ObservableProperty] private string _selectedTheme = "Light";
    [ObservableProperty] private bool _useGpuAcceleration = false;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private string _downloadStatusText = string.Empty;
    [ObservableProperty] private float _downloadProgress;

    public string[] Themes => new[] { "Light", "Dark" };
    public ObservableCollection<ModelDownloadInfo> AvailableModels { get; } = new();

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

        IsDownloading = true;
        DownloadStatusText = $"Downloading {model.Name}...";
        _cts = new CancellationTokenSource();

        var progress = new Progress<int>(p =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                DownloadProgress = p;
                DownloadStatusText = $"Downloading {model.Name}... {p}%";
            });
        });

        try
        {
            await _downloader.DownloadModelAsync(model, progress, _cts.Token);

            Application.Current.Dispatcher.Invoke(() =>
            {
                DownloadStatusText = $"Downloaded {model.Name} to {model.LocalPath}";
                DownloadProgress = 0;
                RefreshModels();
            });
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText = "Download cancelled";
        }
        catch (Exception ex)
        {
            DownloadStatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _cts?.Cancel();
}
