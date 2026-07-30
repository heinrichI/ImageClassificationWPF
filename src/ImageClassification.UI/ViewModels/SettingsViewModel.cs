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

    public SettingsViewModel(IModelDownloader downloader)
    {
        _downloader = downloader;
        RefreshModels();
    }

    [ObservableProperty] private int _onnxThreadCount = 4;
    [ObservableProperty] private string _selectedTheme = "Light";
    [ObservableProperty] private bool _useGpuAcceleration = false;


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
