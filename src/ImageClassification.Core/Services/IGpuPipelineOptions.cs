namespace ImageClassification.Core.Services;

/// <summary>
/// GPU-side pipeline options: how many images go into one ONNX inference batch.
/// Consumed by <see cref="BatchImageEncoder"/> (ONNX call size) and by the comic
/// search pipeline (queue capacity, flush threshold). Values are read live, so a
/// change takes effect from the next search / next batch — no restart needed.
/// </summary>
public interface IGpuPipelineOptions
{
    /// <summary>
    /// Number of images per ONNX inference batch.
    /// Larger = higher GPU utilization but more VRAM per call; values &lt; 1 fall back to 32.
    /// </summary>
    int BatchSize { get; }
}