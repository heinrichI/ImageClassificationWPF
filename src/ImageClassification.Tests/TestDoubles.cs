using ImageClassification.Core.Services;
using ImageClassification.UI.Configuration;

namespace ImageClassification.Tests;

/// <summary>
/// Test double for <see cref="IOnnxRuntimeOptions"/>: returns 0 ("ONNX Runtime default")
/// unless a specific thread count is set.
/// </summary>
internal sealed class FakeOnnxRuntimeOptions : IOnnxRuntimeOptions
{
    public int ThreadCount { get; set; } = 0;
}

/// <summary>
/// Test double for <see cref="IGpuPipelineOptions"/>.
/// </summary>
internal sealed class FakeGpuPipelineOptions : IGpuPipelineOptions
{
    public int BatchSize { get; set; } = 32;
}

/// <summary>
/// Test double for <see cref="IUserSettingsStore"/>: in-memory values, no file I/O.
/// </summary>
internal sealed class FakeUserSettingsStore : IUserSettingsStore
{
    public int OnnxThreadCount { get; set; } = 4;
    public int GpuBatchSize { get; set; } = 32;
    public int ThumbnailWidth { get; set; } = 180;
    public int ThumbnailHeight { get; set; } = 240;
    public int MaxCachedThumbnails { get; set; } = 400;
    public string File => "fake-user-settings.json";

    public void Save(int onnxThreadCount, int gpuBatchSize, int thumbnailWidth, int maxCachedThumbnails)
    {
        OnnxThreadCount = onnxThreadCount;
        GpuBatchSize = gpuBatchSize;
        ThumbnailWidth = thumbnailWidth;
        MaxCachedThumbnails = maxCachedThumbnails;
    }
}