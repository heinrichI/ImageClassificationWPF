using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ImageClassification.Core.Models;
using ImageClassification.UI.ViewModels;

namespace ImageClassification.UI.Views;

/// <summary>
/// Modal "vector cache maintenance" window (prune / vacuum) with a progress bar and
/// Cancel/Interrupt. Presentation only: all state and commands live in
/// <see cref="VectorCacheMaintenanceViewModel" />; the window stays modal for the whole
/// operation, which also blocks searches from the main window (required for VACUUM,
/// which locks the database exclusively).
/// </summary>
public partial class VectorCacheMaintenanceWindow : Window
{
    public VectorCacheMaintenanceWindow(
        string operationTitle,
        string description,
        Func<CancellationToken, IProgress<VectorCacheMaintenanceStatus>, Task<VectorCacheMaintenanceResult>> work)
    {
        InitializeComponent();
        DataContext = new VectorCacheMaintenanceViewModel(operationTitle, description, work);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}