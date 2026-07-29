namespace ImageClassification.Core.Helpers;

public class ProgressReporter<T>
{
    private readonly Action<T>? _onProgress;
    private readonly SynchronizationContext? _syncContext;

    public ProgressReporter(Action<T>? onProgress)
    {
        _onProgress = onProgress;
        _syncContext = SynchronizationContext.Current;
    }

    public void Report(T value)
    {
        if (_syncContext != null)
        {
            _syncContext.Post(_ => _onProgress?.Invoke(value), null);
        }
        else
        {
            _onProgress?.Invoke(value);
        }
    }
}
