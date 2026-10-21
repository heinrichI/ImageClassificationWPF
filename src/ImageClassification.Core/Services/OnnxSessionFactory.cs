using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace ImageClassification.Core.Services;

/// <summary>
/// Shared ONNX session creation with graceful CUDA → CPU fallback. Some ONNX Runtime
/// versions throw on <c>AppendExecutionProvider_CUDA</c> when CUDA/cuDNN libraries are
/// missing, others only when the session is actually created — both cases are handled:
/// the first session attempt uses the CUDA execution provider, and any failure falls
/// back to a fresh CPU-only session (search keeps working) and reports the fallback
/// via <see cref="IGpuFallbackNotifier"/> so the UI can offer the CUDA/cuDNN path dialog.
/// </summary>
internal static class OnnxSessionFactory
{
    /// <summary>
    /// Creates an inference session for <paramref name="modelPath"/>: CUDA EP first
    /// (threads applied), CPU-only on any CUDA-related failure.
    /// </summary>
    public static InferenceSession CreateWithCudaFallback(
        string modelPath,
        int threadCount,
        ILogger? logger,
        string owner,
        IGpuFallbackNotifier? notifier)
    {
        // Try the CUDA execution provider. In some ORT versions the missing CUDA/cuDNN
        // libraries surface here, in others only at session creation below.
        var cudaOptions = BuildOptions(threadCount);
        bool cudaAttached;
        try
        {
            cudaOptions.AppendExecutionProvider_CUDA();
            cudaAttached = true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                "{Owner}: CUDA EP not available ({Message}); falling back to CPU",
                owner, ex.Message);
            notifier?.ReportCpuFallback(owner, ex.Message);
            cudaAttached = false;
        }

        if (cudaAttached)
        {
            try
            {
                var session = new InferenceSession(modelPath, cudaOptions);
                logger?.LogInformation("{Owner}: using CUDA GPU", owner);
                return session;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(
                    "{Owner}: CUDA session creation failed ({Message}); falling back to CPU",
                    owner, ex.Message);
                notifier?.ReportCpuFallback(owner, ex.Message);
            }
        }

        return new InferenceSession(modelPath, BuildOptions(threadCount));
    }

    private static SessionOptions BuildOptions(int threadCount)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        OnnxSessionOptionsHelper.ApplyThreads(options, threadCount);
        return options;
    }
}