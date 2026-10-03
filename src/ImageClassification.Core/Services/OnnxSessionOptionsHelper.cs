using Microsoft.ML.OnnxRuntime;

namespace ImageClassification.Core.Services;

/// <summary>
/// Applies user-configured ONNX Runtime thread counts to <see cref="SessionOptions"/>.
/// Shared by all ONNX session creation sites (encoders, classifier, feature extractor).
/// </summary>
internal static class OnnxSessionOptionsHelper
{
    /// <summary>
    /// Sets intra-op and inter-op thread count.
    /// Values &lt;= 0 leave ONNX Runtime defaults in place.
    /// </summary>
    public static void ApplyThreads(SessionOptions options, int threadCount)
    {
        if (threadCount > 0)
        {
            options.IntraOpNumThreads = threadCount;
            options.InterOpNumThreads = threadCount;
        }
    }
}