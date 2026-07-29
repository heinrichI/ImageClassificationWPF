using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImageClassification.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
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
