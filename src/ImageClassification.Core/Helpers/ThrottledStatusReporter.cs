namespace ImageClassification.Core.Helpers;

/// <summary>
/// Cancellation-safe status reporter with a minimum 250 ms interval between reports.
/// Used for high-frequency detail lines (pipeline snapshots, queue depths) so a burst of
/// producer/consumer events can't flood the UI thread. Coarse phase transitions are
/// reported on the raw status channel and stay immediate. Shared by the comic cover
/// and plain-image search pipelines (generic over the status type).
/// </summary>
public sealed class ThrottledStatusReporter<T>
{
    private static readonly long MinIntervalTicks = 250 * TimeSpan.TicksPerMillisecond;

    private readonly IProgress<T>? _status;
    private readonly CancellationToken _ct;
    private long _lastReportTicks; // 0 = never reported (tick counts are positive)

    public ThrottledStatusReporter(IProgress<T>? status, CancellationToken ct)
    {
        _status = status;
        _ct = ct;
    }

    public void Report(T update)
    {
        if (_status is null || _ct.IsCancellationRequested)
            return;

        long now = DateTime.UtcNow.Ticks;
        long last = Interlocked.Read(ref _lastReportTicks);
        if (now - last < MinIntervalTicks)
            return;

        // Only one caller wins the slot; losers skip this tick
        if (Interlocked.CompareExchange(ref _lastReportTicks, now, last) != last)
            return;

        _status.Report(update);
    }
}