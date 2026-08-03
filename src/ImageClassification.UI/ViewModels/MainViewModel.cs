using System.Collections.ObjectModel;
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
    public ComicCoverSearchViewModel ComicCoverSearch { get; }

    public MainViewModel(
        TrainViewModel train,
        EvaluateViewModel evaluate,
        ClassifyViewModel classify,
        AnalyzeViewModel analyze,
        SettingsViewModel settings,
        ComicCoverSearchViewModel comicCoverSearch)
    {
        Train = train;
        Evaluate = evaluate;
        Classify = classify;
        Analyze = analyze;
        Settings = settings;
        ComicCoverSearch = comicCoverSearch;

        RebuildMenu();
    }

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>
    /// Menu items contributed by the currently active tab ViewModel.
    /// </summary>
    public ObservableCollection<TabMenuItem> ActiveMenuItems { get; } = new();

    partial void OnSelectedTabIndexChanged(int value)
    {
        RebuildMenu();
    }

    private void RebuildMenu()
    {
        ActiveMenuItems.Clear();

        object? activeTab = SelectedTabIndex switch
        {
            0 => Train,
            1 => Evaluate,
            2 => Classify,
            3 => Analyze,
            4 => Settings,
            5 => ComicCoverSearch,
            _ => null
        };

        if (activeTab is ITabMenuProvider provider)
        {
            foreach (var item in provider.MenuItems)
            {
                ActiveMenuItems.Add(item);
            }
        }
    }

    [RelayCommand]
    private void About()
    {
        StatusMessage = "Image Classification — EfficientNet/ConvNeXt/MobileNetV3";
    }
}
