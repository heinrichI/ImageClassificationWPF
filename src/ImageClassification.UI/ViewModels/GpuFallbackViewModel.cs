using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageClassification.UI.Configuration;
using ImageClassification.UI.Services;

namespace ImageClassification.UI.ViewModels;

/// <summary>
/// Drives the "GPU (CUDA) not available" window: two path fields (CUDA bin / cuDNN bin),
/// Browse buttons and Save. Empty fields are auto-filled in the background by
/// <see cref="Services.GpuLibraryDetector"/> (deep detection, which may scan drives);
/// found values are persisted immediately so a restart activates them even if the user
/// just closes the window. Paths apply after the app restart — the native ONNX Runtime
/// DLLs (and the process PATH) are already in place by then.
/// </summary>
public partial class GpuFallbackViewModel : ObservableObject
{
    private readonly IUserSettingsStore _store;

    private string _cudaPath;
    private string _cudnnPath;
    private string _statusText = "Detecting CUDA/cuDNN in typical install locations…";

    public GpuFallbackViewModel(IUserSettingsStore store)
    {
        _store = store;
        _cudaPath = store.CudaPath;
        _cudnnPath = store.CudnnPath;

        if (string.IsNullOrWhiteSpace(_cudaPath) || string.IsNullOrWhiteSpace(_cudnnPath))
            DetectEmptyFieldsInBackground();
        else
            _statusText = BuildStatus();
    }

    /// <summary>CUDA bin directory (the folder with <c>cudart64_*.dll</c>).</summary>
    public string CudaPath
    {
        get => _cudaPath;
        set => SetProperty(ref _cudaPath, value);
    }

    /// <summary>cuDNN bin directory (the folder with <c>cudnn*.dll</c>).</summary>
    public string CudnnPath
    {
        get => _cudnnPath;
        set => SetProperty(ref _cudnnPath, value);
    }

    /// <summary>Multi-line detection / save status shown under the fields.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Picks a folder for the CUDA bin field.</summary>
    [RelayCommand]
    private void BrowseCuda()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the CUDA bin directory (folder with cudart64_*.dll)" };
        if (dialog.ShowDialog() == true)
            CudaPath = dialog.FolderName;
    }

    /// <summary>Picks a folder for the cuDNN bin field.</summary>
    [RelayCommand]
    private void BrowseCudnn()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the cuDNN bin directory (folder with cudnn*.dll)" };
        if (dialog.ShowDialog() == true)
            CudnnPath = dialog.FolderName;
    }

    /// <summary>Persists the field values (applies after the app restart).</summary>
    [RelayCommand]
    private void Save()
    {
        _store.SaveGpuPaths(CudaPath, CudnnPath);
        StatusText = BuildStatus() + Environment.NewLine +
                     "Saved to user-settings.json — restart the app to apply the paths.";
    }

    /// <summary>
    /// Deep-detects the still-empty fields in the background (drive scans can take a
    /// while) and persists found values right away.
    /// </summary>
    private void DetectEmptyFieldsInBackground()
    {
        var detectCuda = string.IsNullOrWhiteSpace(_cudaPath);
        var detectCudnn = string.IsNullOrWhiteSpace(_cudnnPath);

        _ = Task.Run(() =>
        {
            string? cudaFound = null, cudnnFound = null;
            try
            {
                if (detectCuda)
                    cudaFound = GpuLibraryDetector.DetectCudaPath();
                if (detectCudnn)
                    cudnnFound = GpuLibraryDetector.DetectCudnnPathFull();
            }
            catch
            {
                // Detection must never break the dialog.
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || !dispatcher.CheckAccess())
            {
                dispatcher?.Invoke(() => ApplyDetectionResults(detectCuda, detectCudnn, cudaFound, cudnnFound));
                return;
            }

            ApplyDetectionResults(detectCuda, detectCudnn, cudaFound, cudnnFound);
        });
    }

    private void ApplyDetectionResults(bool detectCuda, bool detectCudnn, string? cudaFound, string? cudnnFound)
    {
        var changed = false;
        if (detectCuda && cudaFound is not null)
        {
            CudaPath = cudaFound;
            changed = true;
        }
        if (detectCudnn && cudnnFound is not null)
        {
            CudnnPath = cudnnFound;
            changed = true;
        }

        if (changed)
        {
            // Persist immediately: a restart activates the found paths even if the
            // user just closes the window without pressing Save.
            _store.SaveGpuPaths(CudaPath, CudnnPath);
        }

        var lines = new List<string>();
        if (detectCuda)
            lines.Add(cudaFound is not null
                ? $"CUDA: detected: {cudaFound} (saved)"
                : "CUDA: not found in the registry or typical install locations");
        if (detectCudnn)
            lines.Add(cudnnFound is not null
                ? $"cuDNN: detected: {cudnnFound} (saved)"
                : "cuDNN: not found in the registry or typical install locations");

        StatusText = BuildStatus() + (lines.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, lines) : string.Empty);
    }

    private string BuildStatus()
    {
        var lines = new List<string>();
        lines.Add(Describe("CUDA", CudaPath, GpuLibraryDetector.HasCudart));
        lines.Add(Describe("cuDNN", CudnnPath, GpuLibraryDetector.ContainsCudnn));
        lines.Add("The paths take effect after the app is restarted.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string Describe(string label, string path, Func<string, bool> marker)
    {
        if (string.IsNullOrWhiteSpace(path))
            return $"{label}: (not set)";
        return marker(path)
            ? $"{label}: {path}"
            : $"{label}: {path} — folder not found or missing the marker DLLs";
    }
}