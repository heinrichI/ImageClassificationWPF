using ImageClassification.Core.Services;

namespace ImageClassification.UI.Services;

/// <summary>
/// UI-side <see cref="IGpuFallbackNotifier"/>: forwards the encoders' CUDA → CPU fallback
/// reports as an event. The event may fire on a background thread (the encoder thread
/// that created the session) — subscribers marshal it to the UI thread themselves.
/// </summary>
internal sealed class GpuFallbackNotifier : IGpuFallbackNotifier
{
    /// <summary>Raised when any ONNX encoder fell back from CUDA to CPU.</summary>
    public event Action<string, string>? CpuFallbackReported;

    public void ReportCpuFallback(string owner, string reason)
        => CpuFallbackReported?.Invoke(owner, reason);
}