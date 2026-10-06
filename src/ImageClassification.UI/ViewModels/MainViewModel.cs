using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Shell;
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
    public SearchImageViewModel SearchImage { get; }

    public MainViewModel(
        TrainViewModel train,
        EvaluateViewModel evaluate,
        ClassifyViewModel classify,
        AnalyzeViewModel analyze,
        SettingsViewModel settings,
        ComicCoverSearchViewModel comicCoverSearch,
        SearchImageViewModel searchImage)
    {
        Train = train;
        Evaluate = evaluate;
        Classify = classify;
        Analyze = analyze;
        Settings = settings;
        ComicCoverSearch = comicCoverSearch;
        SearchImage = searchImage;

        // The window taskbar shows the progress of whichever search tab is running:
        // merge both tabs' taskbar state into the single window TaskbarItemInfo binding.
        ComicCoverSearch.PropertyChanged += OnSearchTabProgressChanged;
        SearchImage.PropertyChanged += OnSearchTabProgressChanged;

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
            6 => SearchImage,
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

    // ── Merged taskbar progress (window TaskbarItemInfo is bound to these properties) ──

    [ObservableProperty]
    private double _taskbarProgressValue;

    [ObservableProperty]
    private TaskbarItemProgressState _taskbarProgressState = TaskbarItemProgressState.None;

    private void OnSearchTabProgressChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("TaskbarProgressValue" or "TaskbarProgressState"))
            return;

        // Show the busy tab's progress; if both are idle, hide the bar.
        if (SearchImage.IsBusy)
        {
            TaskbarProgressState = SearchImage.TaskbarProgressState;
            TaskbarProgressValue = SearchImage.TaskbarProgressValue;
        }
        else if (ComicCoverSearch.IsBusy)
        {
            TaskbarProgressState = ComicCoverSearch.TaskbarProgressState;
            TaskbarProgressValue = ComicCoverSearch.TaskbarProgressValue;
        }
        else
        {
            TaskbarProgressState = TaskbarItemProgressState.None;
            TaskbarProgressValue = 0;
        }
    }
}
