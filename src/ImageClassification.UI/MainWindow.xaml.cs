using System.Windows;
using ImageClassification.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ImageClassification.UI;

public partial class MainWindow : Window
{
    private readonly IServiceProvider _services;

    public MainWindow(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
    }

    private void TabControl_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MainTabControl.SelectedIndex < 0) return;

        // Lazy-resolve ViewModels from DI container
        switch (MainTabControl.SelectedIndex)
        {
            case 0: // Training
                if (TrainViewControl.DataContext == null)
                    TrainViewControl.DataContext = _services.GetRequiredService<TrainViewModel>();
                break;
            case 1: // Evaluation
                if (EvaluateViewControl.DataContext == null)
                    EvaluateViewControl.DataContext = _services.GetRequiredService<EvaluateViewModel>();
                break;
            case 2: // Classify & Sort
                if (ClassifyViewControl.DataContext == null)
                    ClassifyViewControl.DataContext = _services.GetRequiredService<ClassifyViewModel>();
                break;
            case 3: // Analyze
                if (AnalyzeViewControl.DataContext == null)
                    AnalyzeViewControl.DataContext = _services.GetRequiredService<AnalyzeViewModel>();
                break;
            case 4: // Settings
                if (SettingsViewControl.DataContext == null)
                    SettingsViewControl.DataContext = _services.GetRequiredService<SettingsViewModel>();
                break;
        }
    }
}
