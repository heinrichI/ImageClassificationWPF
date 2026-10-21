using System.IO;
using System.Windows;
using ImageClassification.Core.Services;
using ImageClassification.UI.Configuration;
using ImageClassification.UI.Services;
using ImageClassification.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImageClassification.UI;

public partial class App : Application
{
    private readonly IHost _host;

    /// <summary>The "GPU not available" window is shown at most once per launch.</summary>
    private bool _gpuFallbackWindowShown;

    public App()
    {
        _host = AppServiceFactory.Host;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();

        // Diagnostic heartbeat (only when ICS_LOG_FILE is set): a line every 5 s from the
        // WPF dispatcher thread — if the lines stop, the dispatcher is blocked and the
        // "UI freeze" is not a slow background operation.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ICS_LOG_FILE")))
        {
            var heartbeatLogger = _host.Services.GetRequiredService<ILogger<App>>();
            var heartbeat = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            heartbeat.Tick += (_, _) => heartbeatLogger.LogInformation("[UI] dispatcher heartbeat");
            heartbeat.Start();
        }

        var store = _host.Services.GetRequiredService<IUserSettingsStore>();

        // GPU paths (user-settings.json): empty values are auto-detected from the NVIDIA
        // registry / Program Files (fast, synchronous), and the resulting bin directories
        // are prepended to the process PATH so the ONNX Runtime CUDA execution provider
        // can find CUDA/cuDNN. ONNX sessions are created lazily (first search / analyze),
        // so this always happens before the native ONNX Runtime DLLs are loaded.
        ApplyGpuPaths(store);

        // Ensure model files exist on disk (do NOT load into memory)
        var initializer = _host.Services.GetRequiredService<IAppInitializationService>();
        await initializer.InitializeAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _host.Services.GetRequiredService<ViewModels.MainViewModel>();
        mainWindow.Show();

        base.OnStartup(e);

        // No CUDA found or configured at all — offer the path dialog (empty fields are
        // auto-filled in the background by the deep detector).
        if (string.IsNullOrWhiteSpace(store.CudaPath))
            ShowGpuFallbackWindow(store);

        // Runtime fallback: CUDA is configured, but ONNX Runtime could not load the
        // libraries when a session was created (reported from a background thread).
        var notifier = _host.Services.GetRequiredService<Services.GpuFallbackNotifier>();
        notifier.CpuFallbackReported += (_, _) => ShowGpuFallbackWindow(store);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        using (_host)
        {
            await _host.StopAsync();
        }

        base.OnExit(e);
    }

    /// <summary>
    /// Fills empty CUDA/cuDNN paths from the auto-detector, persists them to
    /// user-settings.json and prepends the valid bin directories to the process PATH
    /// (the CUDA execution provider resolves cudart/cublas/cudnn via PATH). When cuDNN
    /// is not in a typical location, a deep per-drive scan runs in the background and
    /// persists the result for the NEXT launch (a scan can take a while on large drives).
    /// </summary>
    private static void ApplyGpuPaths(IUserSettingsStore store)
    {
        var cuda = store.CudaPath;
        var cudnn = store.CudnnPath;
        var changed = false;

        if (string.IsNullOrWhiteSpace(cuda))
        {
            cuda = GpuLibraryDetector.DetectCudaPath() ?? string.Empty;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(cudnn))
        {
            cudnn = GpuLibraryDetector.DetectCudnnPathFast() ?? string.Empty;
            changed = true;
        }
        if (changed)
            store.SaveGpuPaths(cuda, cudnn);

        var prepend = new List<string>();
        if (!string.IsNullOrWhiteSpace(cuda) && Directory.Exists(cuda))
            prepend.Add(cuda);
        if (!string.IsNullOrWhiteSpace(cudnn) && Directory.Exists(cudnn))
            prepend.Add(cudnn);

        if (prepend.Count > 0)
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            Environment.SetEnvironmentVariable("PATH", string.Join(";", prepend) + ";" + currentPath);
        }

        if (string.IsNullOrEmpty(store.CudnnPath))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var found = GpuLibraryDetector.DetectCudnnPathFull();
                    if (found is not null)
                        store.SaveGpuPaths(store.CudaPath, found); // applies from the next launch
                }
                catch
                {
                    // Detection must never break startup.
                }
            });
        }
    }

    private void ShowGpuFallbackWindow(IUserSettingsStore store)
    {
        // The fallback report may arrive from the encoder's background thread; the dialog
        // must be created and shown on the WPF dispatcher thread.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowGpuFallbackWindow(store));
            return;
        }

        if (_gpuFallbackWindowShown)
            return;

        try
        {
            _gpuFallbackWindowShown = true;
            new GpuFallbackWindow(store).ShowDialog();
        }
        catch
        {
            // A dialog problem must never break startup; allow the next fallback report
            // to try again.
            _gpuFallbackWindowShown = false;
        }
    }
}