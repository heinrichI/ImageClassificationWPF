using System.Collections.Concurrent;
using ImageClassification.Core.Helpers;
using ImageClassification.Core.Models;

namespace ImageClassification.Core.Services;

/// <summary>
/// CLIP-based zero-shot tag generation.
/// Uses shared IClipImageEncoder (cached) for image embeddings
/// and IClipTextEncoder for proper CLIP text embeddings.
/// </summary>
internal sealed class ClipTagService : ITagService
{
    private readonly IClipImageEncoder _clipEncoder;
    private readonly IClipTextEncoder _textEncoder;

    public ClipTagService(IClipImageEncoder clipEncoder, IClipTextEncoder textEncoder)
    {
        _clipEncoder = clipEncoder ?? throw new ArgumentNullException(nameof(clipEncoder));
        _textEncoder = textEncoder ?? throw new ArgumentNullException(nameof(textEncoder));
    }

    public Task LoadModelAsync(string clipOnnxPath)
    {
        // No-op — model loading is delegated to IClipImageEncoder
        return Task.CompletedTask;
    }

    public async Task<List<ImageTagResult>> GenerateTagsAsync(
        List<string> imagePaths,
        string[] candidateTags,
        int topK = 5,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (imagePaths.Count == 0 || candidateTags.Length == 0)
            return new List<ImageTagResult>();

        // Pre-compute text embeddings for ALL candidate tags (Step 4 optimisation)
        var textEmbeddings = new Dictionary<string, float[]>(candidateTags.Length);
        foreach (var tag in candidateTags)
        {
            var trimmed = tag.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            textEmbeddings[trimmed] = await _textEncoder.EncodeTextAsync(trimmed)
                .ConfigureAwait(false);
        }

        var results = new ConcurrentBag<ImageTagResult>();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken
        };
        int completed = 0;

        try
        {
            await Parallel.ForEachAsync(imagePaths, parallelOptions, async (imagePath, ct) =>
            {
                try
                {
                    var imageEmbedding = await _clipEncoder.EncodeImageAsync(imagePath, ct)
                        .ConfigureAwait(false);

                    var tagScores = new List<(string Tag, float Score)>();
                    foreach (var (tag, textEmb) in textEmbeddings)
                    {
                        float similarity = VectorMath.CosineSimilarity(imageEmbedding, textEmb);
                        tagScores.Add((tag, similarity));
                    }

                    var topTags = tagScores
                        .OrderByDescending(t => t.Score)
                        .Take(topK)
                        .ToList();

                    results.Add(new ImageTagResult
                    {
                        FilePath = imagePath,
                        Tags = topTags
                    });
                }
                catch { /* Skip unprocessable images */ }

                var count = Interlocked.Increment(ref completed);
                progress?.Report(count);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation takes effect between images: return whatever finished so far
            // so the caller can show partial results.
        }

        return results.OrderBy(r => imagePaths.IndexOf(r.FilePath)).ToList();
    }

    public void Dispose()
    {
        // IClipImageEncoder and IClipTextEncoder are shared — don't dispose them here
        GC.SuppressFinalize(this);
    }
}