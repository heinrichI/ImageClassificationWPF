using ImageClassification.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ImageClassification.Core.Services;

/// <summary>
/// Decorator for IClipImageEncoder that transparently caches embeddings in IVectorStore.
/// Cache validity is checked via file LastWriteTimeUtc + file size.
/// </summary>
internal sealed class CachedClipImageEncoder : IClipImageEncoder
{
    private const string ModelName = "ClipViTB32";

    private readonly ClipImageEncoder _inner;
    private readonly IVectorStore _store;
    private readonly ILogger<CachedClipImageEncoder> _logger;

    public CachedClipImageEncoder(ClipImageEncoder inner, IVectorStore store,
        ILogger<CachedClipImageEncoder> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<float[]> EncodeImageAsync(string imagePath, CancellationToken ct = default)
    {
        var meta = GetFileMeta(imagePath);
        var cached = await _store.GetAsync(ModelName, imagePath, meta.LastModified, meta.FileSize)
            .ConfigureAwait(false);

        if (cached is not null)
            return cached;

        _logger.LogDebug("Cache miss for {ModelName}: {Path}", ModelName, imagePath);

        var result = await _inner.EncodeImageAsync(imagePath, ct).ConfigureAwait(false);

        try
        {
            await _store.SaveAsync(ModelName, imagePath, meta.LastModified, meta.FileSize, result)
                .ConfigureAwait(false);
        }
        catch { /* best-effort caching */ }

        return result;
    }

    public void Dispose() => _inner.Dispose();

    private static (DateTime LastModified, long FileSize) GetFileMeta(string path)
    {
        var info = new FileInfo(path);
        return (info.LastWriteTimeUtc, info.Length);
    }
}
