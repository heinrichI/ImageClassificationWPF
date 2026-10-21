namespace ImageClassification.Core.Services;

/// <summary>
/// Notification sink for the case where an ONNX session had to fall back from the CUDA
/// execution provider to CPU — CUDA/cuDNN libraries were not found when the session
/// was created. The UI raises the "GPU not available" dialog from this signal; consumers
/// that don't care (and tests) may register no implementation at all, since encoders
/// accept a null notifier.
/// </summary>
public interface IGpuFallbackNotifier
{
    /// <summary>
    /// Raised (possibly on a background thread) when <paramref name="owner"/> (the encoder
    /// name) fell back to CPU. <paramref name="reason"/> carries the original ONNX Runtime
    /// error message.
    /// </summary>
    void ReportCpuFallback(string owner, string reason);
}