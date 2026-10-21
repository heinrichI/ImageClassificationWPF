using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageClassification.Core.Models;

namespace ImageClassification.UI.ViewModels;

/// <summary>
/// Drives the modal "Vector cache maintenance" window (U2): three visible states —
/// Idle (description + Start), Running (progress bar + Cancel) and Finished
/// (result + Close). The actual maintenance runs off the UI thread via the
/// caller-supplied factory; the window stays modal, so the main window (and any
/// search) is blocked for the duration — required for VACUUM, which locks the
/// database exclusively.
/// </summary>
public partial class VectorCacheMaintenanceViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();

    private string _phase = PhaseIdle;
    private string _phaseText = string.Empty;
    private string _missingText = string.Empty;
    private double _progressValue;
    private double _progressMaximum = 1;
    private bool _isIndeterminate = true;
    private string _resultText = string.Empty;
    private bool _resultSuccess;

    /// <summary>Factory of the maintenance operation: token → progress sink → result.</summary>
    public Func<CancellationToken, IProgress<VectorCacheMaintenanceStatus>, Task<VectorCacheMaintenanceResult>> Work { get; }

    public VectorCacheMaintenanceViewModel(
        string operationTitle,
        string description,
        Func<CancellationToken, IProgress<VectorCacheMaintenanceStatus>, Task<VectorCacheMaintenanceResult>> work)
    {
        OperationTitle = operationTitle;
        Description = description;
        Work = work;
    }

    public string OperationTitle { get; }
    public string Description { get; }

    /// <summary>"Idle" | "Running" | "Finished" — the window toggles panels by it.</summary>
    public string Phase
    {
        get => _phase;
        private set
        {
            if (!SetProperty(ref _phase, value))
                return;

            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsFinished));
            CancelCommand.NotifyCanExecuteChanged();
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    public string PhaseText
    {
        get => _phaseText;
        private set => SetProperty(ref _phaseText, value);
    }

    public string MissingText
    {
        get => _missingText;
        private set => SetProperty(ref _missingText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public double ProgressMaximum
    {
        get => _progressMaximum;
        private set => SetProperty(ref _progressMaximum, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetProperty(ref _isIndeterminate, value);
    }

    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    public bool ResultSuccess
    {
        get => _resultSuccess;
        private set => SetProperty(ref _resultSuccess, value);
    }

    public bool IsIdle => Phase == PhaseIdle;
    public bool IsRunning => Phase == PhaseRunning;
    public bool IsFinished => Phase == PhaseFinished;

    private const string PhaseIdle = "Idle";
    private const string PhaseRunning = "Running";
    private const string PhaseFinished = "Finished";

    /// <summary>Starts the maintenance operation (from the modal window's Start button).</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        if (Phase != PhaseIdle)
            return;

        Phase = PhaseRunning;
        MissingText = string.Empty;
        _ = RunAsync();
    }

    /// <summary>Requests cancellation. Prune aborts before any write; VACUUM is
    /// interrupted and SQLite rolls the rebuild back — both leave the cache intact.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        PhaseText = "Cancelling…";
        _cts.Cancel();
    }

    private bool CanStart => Phase == PhaseIdle;

    private bool CanCancel => Phase == PhaseRunning;

    private async Task RunAsync()
    {
        // Progress<T> is created on the UI thread, so its sink marshals to the dispatcher.
        var progress = new Progress<VectorCacheMaintenanceStatus>(ApplyStatus);
        VectorCacheMaintenanceResult result;
        try
        {
            // No ConfigureAwait: the store work itself runs on pool threads, but the
            // continuation (publishing the Finished state) must land on the UI thread —
            // WPF bindings only accept property changes from the dispatcher.
            result = await Work(_cts.Token, progress);
        }
        catch (OperationCanceledException)
        {
            result = new VectorCacheMaintenanceResult("maintenance", false,
                "Cancelled — no changes were made.");
        }
        catch (Exception ex)
        {
            result = new VectorCacheMaintenanceResult("maintenance", false, ex.Message);
        }

        Phase = PhaseFinished;
        IsIndeterminate = false;
        ResultSuccess = result.Success;
        ResultText = result.Detail;
    }

    private void ApplyStatus(VectorCacheMaintenanceStatus status)
    {
        if (Phase == PhaseFinished || _cts.IsCancellationRequested)
            return;

        if (status.Indeterminate)
        {
            IsIndeterminate = true;
        }
        else
        {
            IsIndeterminate = false;
            ProgressMaximum = Math.Max(1, status.Total);
            ProgressValue = Math.Min(status.Current, ProgressMaximum);
        }

        PhaseText = status.Phase;
        MissingText = status.MissingSoFar > 0
            ? $"{status.MissingSoFar:N0} file(s) missing so far"
            : string.Empty;
    }
}