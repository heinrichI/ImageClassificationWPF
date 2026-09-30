using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageClassification.Core.Models;
using ImageClassification.Core.Services;
using Microsoft.Win32;

namespace ImageClassification.UI.ViewModels;

public partial class TrainViewModel : ObservableObject
{
    private readonly IModelTrainer _trainer;
    private CancellationTokenSource? _cts;

    public TrainViewModel(IModelTrainer trainer)
    {
        _trainer = trainer;
    }

    [ObservableProperty] private string _trainingDirectory = string.Empty;
    [ObservableProperty] private string _saveModelPath = string.Empty;
    [ObservableProperty] private ModelArchitecture _selectedArchitecture = ModelArchitecture.EfficientNetB1;
    [ObservableProperty] private int _imageSize = 224;
    [ObservableProperty] private int _batchSize = 32;
    [ObservableProperty] private int _epochs = 50;
    [ObservableProperty] private float _learningRate = 1e-3f;
    [ObservableProperty] private float _weightDecay = 0.01f;
    [ObservableProperty] private float _validationSplit = 0.2f;
    [ObservableProperty] private OptimizerType _selectedOptimizer = OptimizerType.AdamW;
    [ObservableProperty] private LRSchedule _selectedLRSchedule = LRSchedule.CosineAnnealing;
    [ObservableProperty] private int _warmupEpochs = 5;

    [ObservableProperty] private bool _useTrivialAugment = true;
    [ObservableProperty] private bool _useMixUp = true;
    [ObservableProperty] private float _mixUpAlpha = 0.2f;
    [ObservableProperty] private bool _useCutMix = true;
    [ObservableProperty] private float _cutMixAlpha = 1.0f;
    [ObservableProperty] private float _labelSmoothing = 0.1f;

    [ObservableProperty] private bool _useProgressiveUnfreezing = true;
    [ObservableProperty] private bool _useImageNet21kWeights = false;

    [ObservableProperty] private bool _isTraining;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private float _progressPercent;
    [ObservableProperty] private string _currentEpochText = string.Empty;

    public ObservableCollection<EpochMetrics> TrainingHistory { get; } = new();
    public Array Architectures => Enum.GetValues(typeof(ModelArchitecture));
    public Array Optimizers => Enum.GetValues(typeof(OptimizerType));
    public Array LRSchedules => Enum.GetValues(typeof(LRSchedule));

    partial void OnSelectedArchitectureChanged(ModelArchitecture value)
    {
        ImageSize = value.GetDefaultImageSize();
    }

    [RelayCommand]
    private void BrowseTrainingDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "Select Training Directory" };
        if (dialog.ShowDialog() == true)
            TrainingDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowseSavePath()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "ONNX Model|*.onnx|All Files|*.*",
            DefaultExt = ".onnx",
            FileName = $"{SelectedArchitecture}_model.onnx"
        };
        if (dialog.ShowDialog() == true)
            SaveModelPath = dialog.FileName;
    }

    [RelayCommand]
    private async Task StartTrainingAsync()
    {
        if (string.IsNullOrEmpty(TrainingDirectory) || string.IsNullOrEmpty(SaveModelPath))
        {
            StatusText = "Please select training directory and save path";
            return;
        }

        if (!System.IO.Directory.Exists(TrainingDirectory))
        {
            StatusText = $"Training directory not found: {TrainingDirectory}";
            return;
        }

        var saveDir = System.IO.Path.GetDirectoryName(SaveModelPath);
        if (!string.IsNullOrEmpty(saveDir) && !System.IO.Directory.Exists(saveDir))
        {
            StatusText = $"Directory for save path not found: {saveDir}";
            return;
        }

        IsTraining = true;
        StatusText = "Training...";

        var config = new TrainingConfig
        {
            Architecture = SelectedArchitecture,
            TrainingDirectory = TrainingDirectory,
            SaveModelPath = SaveModelPath,
            ImageSize = ImageSize,
            BatchSize = BatchSize,
            Epochs = Epochs,
            LearningRate = LearningRate,
            WeightDecay = WeightDecay,
            ValidationSplit = ValidationSplit,
            Optimizer = SelectedOptimizer,
            LRSchedule = SelectedLRSchedule,
            WarmupEpochs = WarmupEpochs,
            UseProgressiveUnfreezing = UseProgressiveUnfreezing,
            UseImageNet21kWeights = UseImageNet21kWeights,
            Augmentation = new AugmentationConfig
            {
                UseTrivialAugment = UseTrivialAugment,
                UseMixUp = UseMixUp,
                MixUpAlpha = MixUpAlpha,
                UseCutMix = UseCutMix,
                CutMixAlpha = CutMixAlpha,
                LabelSmoothing = LabelSmoothing
            }
        };

        _cts = new CancellationTokenSource();
        var progress = new Progress<TrainingMetrics>(OnTrainingProgress);

        try
        {
            var result = await _trainer.TrainAsync(config, progress, _cts.Token);
            StatusText = result.Status;
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsTraining = false;
        }
    }

    [RelayCommand]
    private void StopTraining()
    {
        _cts?.Cancel();
        StatusText = "Stopping...";
    }

    private void OnTrainingProgress(TrainingMetrics metrics)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            CurrentEpochText = $"Epoch {metrics.CurrentEpoch}/{metrics.TotalEpochs}";
            ProgressPercent = (float)metrics.CurrentEpoch / metrics.TotalEpochs * 100;
            StatusText = metrics.Status;

            TrainingHistory.Clear();
            foreach (var epoch in metrics.Epochs)
                TrainingHistory.Add(epoch);
        });
    }
}
