using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using Microsoft.Extensions.Logging;

namespace ImageClassification.Core.Services;

/// <summary>
/// Decorator for IImageClassifier that transparently caches classification
/// results in IVectorStore. Cache validity is checked via file
/// LastWriteTimeUtc + file size.  Stores the full softmax probability
/// vector so that ClassificationResult can be reconstructed on cache hit.
/// </summary>
internal sealed class CachedImageClassifier : IImageClassifier
{
    private const string ModelName = "MobileNetV2";

    private readonly OnnxClassifier _inner;
    private readonly IVectorStore _store;
    private readonly ILogger<CachedImageClassifier> _logger;

    public CachedImageClassifier(OnnxClassifier inner, IVectorStore store,
        ILogger<CachedImageClassifier> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ModelInfo Model => _inner.Model;

    public Task LoadModelAsync(string modelPath, string labelFilePath)
        => _inner.LoadModelAsync(modelPath, labelFilePath);

    public ClassificationResult Classify(string imagePath)
    {
        var meta = GetFileMeta(imagePath);
        var cached = _store.GetAsync(ModelName, imagePath, meta.LastModified, meta.FileSize)
            .GetAwaiter().GetResult();

        if (cached is not null)
            return ReconstructResult(imagePath, cached);

        _logger.LogDebug("Cache miss for {ModelName}: {Path}", ModelName, imagePath);

        var result = _inner.Classify(imagePath);

        // Cache the full probability vector (values of AllProbabilities in label order)
        var probs = new float[_inner.Model.ClassLabels.Length];
        for (int i = 0; i < probs.Length; i++)
            probs[i] = result.AllProbabilities.TryGetValue(_inner.Model.ClassLabels[i], out var v) ? v : 0f;

        _store.SaveAsync(ModelName, imagePath, meta.LastModified, meta.FileSize, probs)
            .GetAwaiter().GetResult();

        return result;
    }

    public async Task<List<ClassificationResult>> ClassifyBatchAsync(
        List<string> imagePaths, IProgress<int>? progress = null)
    {
        if (imagePaths.Count == 0)
            return new List<ClassificationResult>();

        // Phase 1: batch cache lookup — one DB round-trip
        var entries = new List<(string FilePath, DateTime LastModified, long FileSize)>(imagePaths.Count);
        foreach (var path in imagePaths)
        {
            try
            {
                var meta = GetFileMeta(path);
                entries.Add((path, meta.LastModified, meta.FileSize));
            }
            catch
            {
                entries.Add((path, DateTime.MinValue, 0));
            }
        }

        Dictionary<string, float[]?> cacheResults;
        try
        {
            cacheResults = await _store.GetBatchAsync(ModelName, entries)
                .ConfigureAwait(false);
        }
        catch
        {
            cacheResults = new Dictionary<string, float[]?>();
        }

        var cacheHits = new List<ClassificationResult>();
        var missPaths = new List<string>();

        foreach (var path in imagePaths)
        {
            if (cacheResults.TryGetValue(path, out var cached) && cached is not null)
            {
                cacheHits.Add(ReconstructResult(path, cached));
            }
            else
            {
                missPaths.Add(path);
            }
        }

        // Phase 2: process misses through the real classifier
        if (missPaths.Count > 0)
        {
            _logger.LogInformation(
                "ClassifyBatch: {MissCount}/{TotalCount} cache misses ({ModelName})",
                missPaths.Count, imagePaths.Count, ModelName);

            var missResults = await _inner.ClassifyBatchAsync(missPaths, progress)
                .ConfigureAwait(false);

            // Save to cache
            foreach (var r in missResults)
            {
                try
                {
                    var meta = GetFileMeta(r.FilePath);
                    var probs = new float[_inner.Model.ClassLabels.Length];
                    for (int i = 0; i < probs.Length; i++)
                        probs[i] = r.AllProbabilities.TryGetValue(_inner.Model.ClassLabels[i], out var v) ? v : 0f;

                    await _store.SaveAsync(ModelName, r.FilePath, meta.LastModified, meta.FileSize, probs)
                        .ConfigureAwait(false);
                }
                catch { /* best-effort caching */ }
            }

            cacheHits.AddRange(missResults);
        }

        return cacheHits.OrderBy(r => imagePaths.IndexOf(r.FilePath)).ToList();
    }

    public void Dispose() => _inner.Dispose();

    // ─── helpers ───

    private static (DateTime LastModified, long FileSize) GetFileMeta(string path)
    {
        var info = new FileInfo(path);
        return (info.LastWriteTimeUtc, info.Length);
    }

    private ClassificationResult ReconstructResult(string imagePath, float[] probs)
    {
        var labels = _inner.Model.ClassLabels;
        var allProbabilities = new Dictionary<string, float>(labels.Length);
        for (int i = 0; i < labels.Length && i < probs.Length; i++)
            allProbabilities[labels[i]] = probs[i];

        var maxIndex = 0;
        var maxVal = probs[0];
        for (int i = 1; i < probs.Length; i++)
        {
            if (probs[i] > maxVal)
            {
                maxVal = probs[i];
                maxIndex = i;
            }
        }

        var predictedClass = maxIndex < labels.Length ? labels[maxIndex] : "Unknown";
        return new ClassificationResult(imagePath, predictedClass, maxVal, allProbabilities);
    }
}
