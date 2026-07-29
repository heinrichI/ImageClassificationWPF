using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImageClassification.UI.ViewModels;

/// <summary>
/// Shell ViewModel and composition root for tab ViewModels.
/// MainWindow binds to this; each tab gets its VM via DataContext="{Binding Train|...}".
/// </summary>
public partial class MainViewModel : ObservableObject
{
    public TrainViewModel Train { get; }
    public EvaluateViewModel Evaluate { get; }
    public ClassifyViewModel Classify { get; }
    public AnalyzeViewModel Analyze { get; }
    public SettingsViewModel Settings { get; }

    public MainViewModel(
        TrainViewModel train,
        EvaluateViewModel evaluate,
        ClassifyViewModel classify,
        AnalyzeViewModel analyze,
        SettingsViewModel settings)
    {
        Train = train;
        Evaluate = evaluate;
        Classify = classify;
        Analyze = analyze;
        Settings = settings;
    }

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private int _selectedTabIndex;

    [RelayCommand]
    private void About()
    {
        StatusMessage = "Image Classification — EfficientNet/ConvNeXt/MobileNetV3";
    }
}
