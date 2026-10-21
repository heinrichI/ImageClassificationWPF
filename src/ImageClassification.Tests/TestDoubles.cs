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
    public int MaxPreviews { get; set; } = 0; // 0 = no limit (tests are unaffected)
    public int SearchMode { get; set; } = 0;
    public IReadOnlyList<string> SearchDirectories { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> SearchPhraseHistory { get; set; } = Array.Empty<string>();
    public double SimilarityThreshold { get; set; } = 0.25;
    public IReadOnlyList<string> ImageSearchDirectories { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> ImageSearchPhraseHistory { get; set; } = Array.Empty<string>();
    public double ImageSearchSimilarityThreshold { get; set; } = 0.25;
    public bool ImageSearchRecurseSubfolders { get; set; } = true;
    public string CudaPath { get; set; } = string.Empty;
    public string CudnnPath { get; set; } = string.Empty;
    public string File => "fake-user-settings.json";

    public void Save(int onnxThreadCount, int gpuBatchSize, int thumbnailWidth, int maxCachedThumbnails, int maxPreviews, int searchMode)
    {
        OnnxThreadCount = onnxThreadCount;
        GpuBatchSize = gpuBatchSize;
        ThumbnailWidth = thumbnailWidth;
        MaxCachedThumbnails = maxCachedThumbnails;
        MaxPreviews = maxPreviews;
        SearchMode = searchMode;
    }

    public void SaveSearchDirectories(IReadOnlyList<string> directories)
    {
        SearchDirectories = directories?.ToList() ?? new List<string>();
    }

    public void SaveSearchPhraseHistory(IReadOnlyList<string> history)
    {
        SearchPhraseHistory = history?.ToList() ?? new List<string>();
    }

    public void SaveSimilarityThreshold(double value)
    {
        SimilarityThreshold = value;
    }

    public void SaveImageSearchDirectories(IReadOnlyList<string> directories)
    {
        ImageSearchDirectories = directories?.ToList() ?? new List<string>();
    }

    public void SaveImageSearchPhraseHistory(IReadOnlyList<string> history)
    {
        ImageSearchPhraseHistory = history?.ToList() ?? new List<string>();
    }

    public void SaveImageSearchSimilarityThreshold(double value)
    {
        ImageSearchSimilarityThreshold = value;
    }

    public void SaveImageSearchRecurseSubfolders(bool value)
    {
        ImageSearchRecurseSubfolders = value;
    }

    public void SaveGpuPaths(string? cudaPath, string? cudnnPath)
    {
        CudaPath = cudaPath?.Trim() ?? string.Empty;
        CudnnPath = cudnnPath?.Trim() ?? string.Empty;
    }
}