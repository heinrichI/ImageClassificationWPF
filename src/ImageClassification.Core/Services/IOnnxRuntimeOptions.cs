namespace ImageClassification.Core.Services;

/// <summary>
/// Live view over user-configured ONNX Runtime execution options.
/// Implemented by the UI layer (settings persisted in <c>user-settings.json</c>
/// in the program directory); core encoders read <see cref="ThreadCount"/>
/// when an ONNX session is created (i.e. on model load/reload).
/// </summary>
public interface IOnnxRuntimeOptions
{
    /// <summary>
    /// Thread count for intra-op and inter-op parallelism.
    /// Values &lt;= 0 mean "let ONNX Runtime decide".
    /// </summary>
    int ThreadCount { get; }
}