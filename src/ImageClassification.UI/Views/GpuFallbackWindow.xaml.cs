using System.Windows;
using ImageClassification.UI.Configuration;
using ImageClassification.UI.ViewModels;

namespace ImageClassification.UI.Views;

/// <summary>
/// Modal "GPU (CUDA) not available" dialog: CUDA/cuDNN bin path selection with background
/// auto-detection. Presentation only — all state and commands live in
/// <see cref="GpuFallbackViewModel" />.
/// </summary>
public partial class GpuFallbackWindow : Window
{
    public GpuFallbackWindow(IUserSettingsStore store)
    {
        InitializeComponent();
        DataContext = new GpuFallbackViewModel(store);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}