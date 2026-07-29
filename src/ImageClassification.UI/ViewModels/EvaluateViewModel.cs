using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
using Microsoft.Win32;

namespace ImageClassification.UI.ViewModels;

public partial class EvaluateViewModel : ObservableObject
{
    private readonly IModelEvaluator _evaluator;

    public EvaluateViewModel(IModelEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    [ObservableProperty] private string _modelPath = string.Empty;
    [ObservableProperty] private string _testDirectory = string.Empty;
    [ObservableProperty] private int _imageSize = 224;
    [ObservableProperty] private int _batchSize = 32;
    [ObservableProperty] private bool _isEvaluating;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private float _progressPercent;
    [ObservableProperty] private string _accuracyText = "—";
    [ObservableProperty] private string _totalImagesText = "—";
    [ObservableProperty] private string _correctText = "—";

    public ObservableCollection<ClassMetrics> PerClassResults { get; } = new();
    public float[,]? ConfusionMatrixData { get; private set; }
    public string[]? ConfusionMatrixLabels { get; private set; }

    [RelayCommand]
    private void BrowseModel()
    {
        var dialog = new OpenFileDialog { Filter = "ONNX Model|*.onnx|All Files|*.*" };
        if (dialog.ShowDialog() == true)
            ModelPath = dialog.FileName;
    }

    [RelayCommand]
    private void BrowseTestDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "Select Test Directory" };
        if (dialog.ShowDialog() == true)
            TestDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private async Task RunEvaluationAsync()
    {
        if (string.IsNullOrEmpty(ModelPath) || string.IsNullOrEmpty(TestDirectory))
        {
            StatusText = "Please select model and test directory";
            return;
        }

        IsEvaluating = true;
        StatusText = "Evaluating...";

        var progress = new Progress<int>(p =>
        {
            Application.Current.Dispatcher.Invoke(() => StatusText = $"Evaluating image {p}...");
        });

        try
        {
            var result = await _evaluator.EvaluateAsync(ModelPath, TestDirectory, ImageSize, BatchSize, progress);

            Application.Current.Dispatcher.Invoke(() =>
            {
                AccuracyText = $"{result.Accuracy:P1}";
                TotalImagesText = result.TotalImages.ToString();
                CorrectText = result.CorrectPredictions.ToString();
                StatusText = $"Done — {result.Accuracy:P1} accuracy on {result.TotalImages} images";

                PerClassResults.Clear();
                foreach (var metric in result.PerClassMetrics.Values)
                    PerClassResults.Add(metric);

                ConfusionMatrixData = result.ConfusionMatrix;
                ConfusionMatrixLabels = result.ClassLabels;
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsEvaluating = false;
        }
    }
}
