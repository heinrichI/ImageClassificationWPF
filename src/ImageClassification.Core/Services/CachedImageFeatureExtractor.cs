using ImageClassification.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ImageClassification.Core.Services;

/// <summary>
/// Decorator for IImageFeatureExtractor that transparently caches embeddings in IVectorStore.
/// Cache validity is checked via file LastWriteTimeUtc + file size.
/// </summary>
internal sealed class CachedImageFeatureExtractor : IImageFeatureExtractor
{
    private const string ModelName = "MobileNetV2";

    private readonly ImageFeatureExtractor _inner;
    private readonly IVectorStore _store;
    private readonly ILogger<CachedImageFeatureExtractor> _logger;

    public CachedImageFeatureExtractor(ImageFeatureExtractor inner, IVectorStore store,
        ILogger<CachedImageFeatureExtractor> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task LoadModelAsync(string modelPath, int imageSize)
        => _inner.LoadModelAsync(modelPath, imageSize);

    public float[] ExtractFeatures(string imagePath)
    {
        var meta = GetFileMeta(imagePath);
        var cached = _store.GetAsync(ModelName, imagePath, meta.LastModified, meta.FileSize)
            .GetAwaiter().GetResult();

        if (cached is not null)
            return cached;

        _logger.LogDebug("Cache miss for {ModelName}: {Path}", ModelName, imagePath);

        var result = _inner.ExtractFeatures(imagePath);

        _store.SaveAsync(ModelName, imagePath, meta.LastModified, meta.FileSize, result)
            .GetAwaiter().GetResult();

        return result;
    }

    public async Task<List<ImageFeatures>> ExtractBatchAsync(
        List<string> imagePaths, IProgress<int>? progress = null)
    {
        // Phase 1: check cache for every path
        var cacheHits = new List<ImageFeatures>();
        var missPaths = new List<string>();

        foreach (var path in imagePaths)
        {
            try
            {
                var meta = GetFileMeta(path);
                var cached = await _store.GetAsync(ModelName, path, meta.LastModified, meta.FileSize)
                    .ConfigureAwait(false);

                if (cached is not null)
                {
                    cacheHits.Add(new ImageFeatures { FilePath = path, Features = cached });
                }
                else
                {
                    missPaths.Add(path);
                }
            }
            catch
            {
                missPaths.Add(path);
            }
        }

        // Phase 2: process misses through the real extractor
        if (missPaths.Count > 0)
        {
            _logger.LogInformation(
                "ExtractBatch: {MissCount}/{TotalCount} cache misses ({ModelName})",
                missPaths.Count, imagePaths.Count, ModelName);

            var missResults = await _inner.ExtractBatchAsync(missPaths, progress)
                .ConfigureAwait(false);

            // Save to cache
            foreach (var mf in missResults)
            {
                try
                {
                    var meta = GetFileMeta(mf.FilePath);
                    await _store.SaveAsync(ModelName, mf.FilePath, meta.LastModified, meta.FileSize, mf.Features)
                        .ConfigureAwait(false);
                }
                catch { /* best-effort caching */ }
            }

            cacheHits.AddRange(missResults);
        }

        return cacheHits.OrderBy(r => imagePaths.IndexOf(r.FilePath)).ToList();
    }

    public void Dispose() => _inner.Dispose();

    private static (DateTime LastModified, long FileSize) GetFileMeta(string path)
    {
        var info = new FileInfo(path);
        return (info.LastWriteTimeUtc, info.Length);
    }
}
