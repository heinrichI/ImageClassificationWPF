using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
using Microsoft.Win32;

namespace ImageClassification.UI.ViewModels;

public partial class ClassifyViewModel : ObservableObject
{
    private readonly IImageSorter _sorter;
    private CancellationTokenSource? _cts;

    public ClassifyViewModel(IImageSorter sorter)
    {
        _sorter = sorter;
    }

    [ObservableProperty] private string _sourceDirectory = string.Empty;
    [ObservableProperty] private float _confidenceThreshold = 0.999f;
    [ObservableProperty] private int _imageSize = 224;
    [ObservableProperty] private int _batchSize = 32;
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private float _progressPercent;
    [ObservableProperty] private string _currentFileText = string.Empty;
    [ObservableProperty] private string _sortedCountText = "0";
    [ObservableProperty] private string _skippedCountText = "0";
    [ObservableProperty] private string _totalCountText = "0";
    [ObservableProperty] private string _conflictsSkippedText = "0";
    [ObservableProperty] private int _timestampWindowMinutes = 1;

    public ObservableCollection<SortProgress> ProcessingLog { get; } = new();
    public ObservableCollection<SortWarning> Warnings { get; } = new();

    [RelayCommand]
    private void BrowseSourceDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "Select Source Directory" };
        if (dialog.ShowDialog() == true)
            SourceDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private async Task SortImagesAsync()
    {
        if (string.IsNullOrEmpty(SourceDirectory))
        {
            StatusText = "Please select a source directory";
            return;
        }

        IsProcessing = true;
        StatusText = "Checking for timestamp conflicts...";
        Warnings.Clear();

        try
        {
            // Detect conflicts first
            var conflicts = await _sorter.DetectTimestampConflictsAsync(
                SourceDirectory,
                ImageSize, TimestampWindowMinutes);

            if (conflicts.Count > 0)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var w in conflicts)
                        Warnings.Add(w);
                });

                var message = $"Found {conflicts.Count} timestamp conflict(s):\n\n";
                foreach (var w in conflicts)
                {
                    message += $"• {w.FilePaths.Count} files within {(w.TimeWindowEnd - w.TimeWindowStart).TotalSeconds:F0}s " +
                               $"predicted as [{string.Join(", ", w.PredictedClasses.Distinct())}]\n";
                }
                message += "\nSkip conflicting files?";

                var answer = Application.Current.Dispatcher.Invoke(() =>
                {
                    return MessageBox.Show(message, "Timestamp Conflicts Detected",
                        MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                });

                if (answer == MessageBoxResult.Cancel)
                {
                    IsProcessing = false;
                    StatusText = "Cancelled";
                    return;
                }

                bool skipConflicts = answer == MessageBoxResult.Yes;

                StatusText = "Sorting...";
                await ExecuteSortAsync(skipConflicts);
            }
            else
            {
                StatusText = "No conflicts found. Sorting...";
                await ExecuteSortAsync(skipConflicts: true);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task ExecuteSortAsync(bool skipConflicts)
    {
        _cts = new CancellationTokenSource();
        var progress = new Progress<SortProgress>(OnSortProgress);

        var result = await _sorter.SortImagesAsync(
            SourceDirectory,
            ConfidenceThreshold, ImageSize, BatchSize,
            moveFiles: false,
            timestampWindowMinutes: TimestampWindowMinutes,
            skipConflicts: skipConflicts,
            progress: progress,
            cancellationToken: _cts.Token);

        Application.Current.Dispatcher.Invoke(() =>
        {
            SortedCountText = result.SortedImages.ToString();
            SkippedCountText = result.SkippedImages.ToString();
            TotalCountText = result.TotalImages.ToString();
            ConflictsSkippedText = result.ConflictsSkipped.ToString();

            if (result.Warnings.Count > 0)
            {
                Warnings.Clear();
                foreach (var w in result.Warnings)
                    Warnings.Add(w);
            }

            var status = $"Done — {result.SortedImages} sorted, {result.SkippedImages} skipped";
            if (result.ConflictsSkipped > 0)
                status += $", {result.ConflictsSkipped} conflict(s) skipped";
            StatusText = status;
        });
    }

    [RelayCommand]
    private void CancelSort() => _cts?.Cancel();

    private void OnSortProgress(SortProgress p)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            ProgressPercent = (float)p.Current / p.Total * 100;
            CurrentFileText = p.CurrentFile;
            if (ProcessingLog.Count > 100)
                ProcessingLog.RemoveAt(0);
            ProcessingLog.Add(p);
        });
    }
}
