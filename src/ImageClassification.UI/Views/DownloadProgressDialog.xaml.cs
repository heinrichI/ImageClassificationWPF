using System.Windows;

namespace ImageClassification.UI.Views;

public partial class DownloadProgressDialog : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<IProgress<int>, CancellationToken, Task> _operation;

    public DownloadProgressDialog(string title, Func<IProgress<int>, CancellationToken, Task> operation)
    {
        InitializeComponent();
        Title = title;
        _operation = operation;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var progress = new Progress<int>(p =>
        {
            Dispatcher.Invoke(() =>
            {
                ProgressBar.Value = p;
                StatusText.Text = $"{Title}... {p}%";
            });
        });

        try
        {
            await Task.Run(() => _operation(progress, _cts.Token));
            Dispatcher.Invoke(() => DialogResult = true);
        }
        catch (OperationCanceledException)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = "Download cancelled";
                DialogResult = false;
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show(this, ex.Message, "Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
                DialogResult = false;
            });
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancelling...";
    }
}
