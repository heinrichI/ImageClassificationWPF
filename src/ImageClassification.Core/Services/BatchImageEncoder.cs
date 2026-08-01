using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageClassification.Core.Services;

/// <summary>
/// Batched CLIP image encoder with producer-consumer pipeline.
/// Preprocesses images in parallel on CPU (ImageSharp), then runs batched ONNX
/// inference on GPU. No caching logic — caller is responsible for cache management.
/// </summary>
internal sealed class BatchImageEncoder : IDisposable
{
    private const int EmbeddingDim = 512;

    private readonly IModelDownloader _downloader;
    private readonly ILogger<BatchImageEncoder> _logger;
    private InferenceSession? _session;
    private int _imageSize = 224;
    private bool _initialized;
    private Task? _initTask;
    private readonly object _initLock = new();

    /// <summary>
    /// Number of images per ONNX inference batch. Larger = higher GPU utilization.
    /// Bound from <see cref="BatchImageEncoderSettings"/>.
    /// </summary>
    public int BatchSize { get; private set; } = 32;

    /// <summary>
    /// Number of batches to prefetch (CPU preprocessing ahead of GPU inference).
    /// Default 2 — keeps GPU fed while minimizing memory pressure.
    /// </summary>
    public int PrefetchCount { get; set; } = 2;

    public BatchImageEncoder(
        IModelDownloader downloader,
        BatchImageEncoderSettings settings,
        ILogger<BatchImageEncoder> logger)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ArgumentNullException.ThrowIfNull(settings);

        if (settings.BatchSize <= 0)
        {
            BatchSize = 32;
            _logger.LogWarning(
                "BatchImageEncoder: invalid BatchSize={BatchSize} in configuration. Fallback to {FallbackBatchSize}.",
                settings.BatchSize, BatchSize);
        }
        else
        {
            BatchSize = settings.BatchSize;
        }
    }

    private Task EnsureInitializedAsync()
    {
        if (_initialized) return Task.CompletedTask;
        lock (_initLock)
        {
            if (_initialized) return Task.CompletedTask;
            _initTask ??= InitializeAsync();
        }
        return _initTask;
    }

    private async Task InitializeAsync()
    {
        var path = _downloader.GetModelPath("clip_vit_b32.onnx");
        await LoadSessionAsync(path);
        _initialized = true;
    }

    private async Task LoadSessionAsync(string modelPath)
    {
        _session?.Dispose();

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        try
        {
            sessionOptions.AppendExecutionProvider_CUDA();
            _logger.LogInformation("BatchImageEncoder: using CUDA GPU");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"BatchImageEncoder: CUDA unavailable, falling back to CPU {ex.Message}");
        }

        _session = new InferenceSession(modelPath, sessionOptions);

        var inputMeta = _session.InputMetadata.First();
        var dims = inputMeta.Value.Dimensions;
        if (dims.Length >= 4 && dims[2] > 0)
            _imageSize = dims[2];

        await Task.CompletedTask;
    }

    /// <summary>
    /// Encodes a list of image paths into L2-normalized embeddings using batched GPU inference.
    /// No caching — caller is responsible for cache management.
    /// Returns embeddings in the same order as <paramref name="imagePaths"/>.
    /// Failed images (e.g. corrupt file) get a zero-filled embedding.
    /// </summary>
    public async Task<float[][]> EncodeImagesBatchAsync(
        List<string> imagePaths,
        IProgress<(int Current, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_session is null, this);

        if (imagePaths.Count == 0)
            return Array.Empty<float[]>();

        var result = new float[imagePaths.Count][];

        if (PrefetchCount == 0)
        {
            ProcessBatchesSequential(imagePaths, result, progress, ct);
            return result;
        }

        var queue = new BlockingCollection<PreparedImageBatch>(PrefetchCount);
        var totalBatches = (imagePaths.Count + BatchSize - 1) / BatchSize;

        // Producer: preprocess images in parallel on CPU
        var producer = Task.Run(() =>
        {
            try
            {
                for (int batchStart = 0; batchStart < imagePaths.Count; batchStart += BatchSize)
                {
                    ct.ThrowIfCancellationRequested();

                    var batchEnd = Math.Min(batchStart + BatchSize, imagePaths.Count);
                    var batchCount = batchEnd - batchStart;
                    var prepared = new (int Index, float[,,]? Tensor)[batchCount];

                    Parallel.For(0, batchCount, new ParallelOptions { CancellationToken = ct }, b =>
                    {
                        int idx = batchStart + b;
                        try
                        {
                            prepared[b] = (idx, PreprocessImage(imagePaths[idx]));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Preprocessing failed: {Path}", imagePaths[idx]);
                            prepared[b] = (idx, null);
                        }
                    });

                    int validCount = 0;
                    for (int b = 0; b < prepared.Length; b++)
                        if (prepared[b].Tensor is not null)
                            validCount++;

                    if (validCount == 0)
                    {
                        for (int b = 0; b < prepared.Length; b++)
                            result[prepared[b].Index] = new float[EmbeddingDim];
                        continue;
                    }

                    var batchTensor = new DenseTensor<float>(new[] { validCount, 3, _imageSize, _imageSize });
                    var batchIndices = new List<int>(validCount);
                    int slot = 0;

                    for (int b = 0; b < prepared.Length; b++)
                    {
                        var (idx, tensor) = prepared[b];
                        if (tensor is null)
                        {
                            result[idx] = new float[EmbeddingDim];
                            continue;
                        }

                        for (int c = 0; c < 3; c++)
                            for (int y = 0; y < _imageSize; y++)
                                for (int x = 0; x < _imageSize; x++)
                                    batchTensor[slot, c, y, x] = tensor[c, y, x];

                        batchIndices.Add(idx);
                        slot++;
                    }

                    queue.Add(new PreparedImageBatch(batchStart, imagePaths.Count, batchIndices, batchTensor));
                }
            }
            finally
            {
                queue.CompleteAdding();
            }
        }, ct);

        // Consumer: run batched GPU inference
        foreach (var batch in queue.GetConsumingEnumerable(ct))
        {
            ct.ThrowIfCancellationRequested();

            _logger.LogDebug("Batch {BatchNum}/{TotalBatches} on GPU ({Count} images)",
                batch.BatchStart / BatchSize + 1, totalBatches, batch.Indices.Count);

            var embeddings = RunBatchedInference(batch.Tensor!);

            for (int b = 0; b < batch.Indices.Count; b++)
            {
                int idx = batch.Indices[b];
                var embedding = new float[EmbeddingDim];
                Array.Copy(embeddings, b * EmbeddingDim, embedding, 0, EmbeddingDim);

                float norm = 0;
                for (int i = 0; i < embedding.Length; i++)
                    norm += embedding[i] * embedding[i];
                norm = MathF.Sqrt(norm);
                if (norm > 0)
                    for (int i = 0; i < embedding.Length; i++)
                        embedding[i] /= norm;

                result[idx] = embedding;
            }

            progress?.Report((batch.BatchStart + batch.Indices.Count, imagePaths.Count));
        }

        try { await producer.ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogError(ex, "Batch producer failed"); }

        return result;
    }

    /// <summary>
    /// Encodes in-memory image bytes into L2-normalized embeddings using batched GPU inference.
    /// Returns embeddings in the same order as <paramref name="items"/>.
    /// Failed images (e.g. corrupt bytes) get a zero-filled embedding.
    /// </summary>
    public async Task<float[][]> EncodeImagesFromMemoryBatchAsync(
        List<(int OriginalIndex, byte[] Data)> items,
        IProgress<(int Current, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_session is null, this);

        if (items.Count == 0)
            return Array.Empty<float[]>();

        var result = new float[items.Count][];

        for (int batchStart = 0; batchStart < items.Count; batchStart += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batchEnd = Math.Min(batchStart + BatchSize, items.Count);
            var batchCount = batchEnd - batchStart;
            var prepared = new (int Index, float[,,]? Tensor)[batchCount];

            Parallel.For(0, batchCount, new ParallelOptions { CancellationToken = ct }, b =>
            {
                int idx = batchStart + b;
                try
                {
                    prepared[b] = (idx, PreprocessImageFromMemory(items[idx].Data));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Preprocessing from memory failed for item index {ItemIndex}", items[idx].OriginalIndex);
                    prepared[b] = (idx, null);
                }
            });

            int validCount = 0;
            for (int b = 0; b < prepared.Length; b++)
                if (prepared[b].Tensor is not null)
                    validCount++;

            if (validCount == 0)
            {
                for (int b = 0; b < prepared.Length; b++)
                    result[prepared[b].Index] = new float[EmbeddingDim];
                progress?.Report((batchEnd, items.Count));
                continue;
            }

            var batchTensor = new DenseTensor<float>(new[] { validCount, 3, _imageSize, _imageSize });
            var batchIndices = new List<int>(validCount);
            int slot = 0;

            for (int b = 0; b < prepared.Length; b++)
            {
                var (idx, tensor) = prepared[b];
                if (tensor is null)
                {
                    result[idx] = new float[EmbeddingDim];
                    continue;
                }

                for (int c = 0; c < 3; c++)
                    for (int y = 0; y < _imageSize; y++)
                        for (int x = 0; x < _imageSize; x++)
                            batchTensor[slot, c, y, x] = tensor[c, y, x];

                batchIndices.Add(idx);
                slot++;
            }

            var embeddings = RunBatchedInference(batchTensor);

            for (int b = 0; b < batchIndices.Count; b++)
            {
                int idx = batchIndices[b];
                var embedding = new float[EmbeddingDim];
                Array.Copy(embeddings, b * EmbeddingDim, embedding, 0, EmbeddingDim);

                float norm = 0;
                for (int i = 0; i < embedding.Length; i++)
                    norm += embedding[i] * embedding[i];
                norm = MathF.Sqrt(norm);
                if (norm > 0)
                    for (int i = 0; i < embedding.Length; i++)
                        embedding[i] /= norm;

                result[idx] = embedding;
            }

            progress?.Report((batchEnd, items.Count));
        }

        return result;
    }

    /// <summary>
    /// Runs a single batched ONNX inference.
    /// PixelValues shape: [batch, 3, H, W]. Returns flattened [batch * 512].
    /// </summary>
    private float[] RunBatchedInference(DenseTensor<float> pixelValues)
    {
        var textInput = new DenseTensor<long>(new[] { 1, 77 });
        for (int i = 0; i < 77; i++)
            textInput[0, i] = 49407L;

        var inputMeta = _session!.InputMetadata;
        var textInputName = inputMeta.First(kvp => kvp.Value.ElementDataType == TensorElementType.Int64).Key;
        var imageInputName = inputMeta.First(kvp => kvp.Value.ElementDataType == TensorElementType.Float).Key;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(textInputName, textInput),
            NamedOnnxValue.CreateFromTensor(imageInputName, pixelValues)
        };

        var attentionMaskInput = inputMeta
            .FirstOrDefault(kvp => kvp.Key.Contains("attention_mask", StringComparison.OrdinalIgnoreCase));
        if (attentionMaskInput.Key is not null)
        {
            var attentionMask = new DenseTensor<long>(new[] { 1, 77 });
            for (int i = 0; i < 77; i++)
                attentionMask[0, i] = 0;
            inputs.Add(NamedOnnxValue.CreateFromTensor(attentionMaskInput.Key, attentionMask));
        }

        using var results = _session.Run(inputs);
        return results.First(r => r.Name == "image_embeds").AsTensor<float>().ToArray();
    }

    /// <summary>
    /// Fallback: process batches without prefetching.
    /// </summary>
    private void ProcessBatchesSequential(
        List<string> imagePaths,
        float[][] result,
        IProgress<(int Current, int Total)>? progress,
        CancellationToken ct)
    {
        for (int batchStart = 0; batchStart < imagePaths.Count; batchStart += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batchEnd = Math.Min(batchStart + BatchSize, imagePaths.Count);
            var batchCount = batchEnd - batchStart;
            var prepared = new (int Index, float[,,]? Tensor)[batchCount];

            Parallel.For(0, batchCount, new ParallelOptions { CancellationToken = ct }, b =>
            {
                int idx = batchStart + b;
                try
                {
                    prepared[b] = (idx, PreprocessImage(imagePaths[idx]));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Preprocessing failed: {Path}", imagePaths[idx]);
                    prepared[b] = (idx, null);
                }
            });

            int validCount = 0;
            for (int b = 0; b < prepared.Length; b++)
                if (prepared[b].Tensor is not null)
                    validCount++;

            if (validCount == 0)
            {
                for (int b = 0; b < prepared.Length; b++)
                    result[prepared[b].Index] = new float[EmbeddingDim];
                continue;
            }

            var batchTensor = new DenseTensor<float>(new[] { validCount, 3, _imageSize, _imageSize });
            var batchIndices = new List<int>(validCount);
            int slot = 0;

            for (int b = 0; b < prepared.Length; b++)
            {
                var (idx, tensor) = prepared[b];
                if (tensor is null)
                {
                    result[idx] = new float[EmbeddingDim];
                    continue;
                }

                for (int c = 0; c < 3; c++)
                    for (int y = 0; y < _imageSize; y++)
                        for (int x = 0; x < _imageSize; x++)
                            batchTensor[slot, c, y, x] = tensor[c, y, x];

                batchIndices.Add(idx);
                slot++;
            }

            var embeddings = RunBatchedInference(batchTensor);

            for (int b = 0; b < batchIndices.Count; b++)
            {
                int idx = batchIndices[b];
                var embedding = new float[EmbeddingDim];
                Array.Copy(embeddings, b * EmbeddingDim, embedding, 0, EmbeddingDim);

                float norm = 0;
                for (int i = 0; i < embedding.Length; i++)
                    norm += embedding[i] * embedding[i];
                norm = MathF.Sqrt(norm);
                if (norm > 0)
                    for (int i = 0; i < embedding.Length; i++)
                        embedding[i] /= norm;

                result[idx] = embedding;
            }

            progress?.Report((batchStart + batchIndices.Count, imagePaths.Count));
        }
    }

    /// <summary>
    /// Loads an image from disk, resizes to 224x224, and applies CLIP normalization.
    /// Returns a float[3, 224, 224] tensor.
    /// </summary>
    private static float[,,] PreprocessImage(string imagePath)
    {
        using var image = Image.Load<Rgb24>(imagePath);
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(224, 224),
            Mode = ResizeMode.Crop
        }));

        var tensor = new float[3, 224, 224];
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < 224; y++)
            {
                var pixelRow = accessor.GetRowSpan(y);
                for (int x = 0; x < 224; x++)
                {
                    var pixel = pixelRow[x];
                    tensor[0, y, x] = (pixel.R / 255f - 0.48145466f) / 0.26862954f;
                    tensor[1, y, x] = (pixel.G / 255f - 0.4578275f) / 0.26130258f;
                    tensor[2, y, x] = (pixel.B / 255f - 0.40821073f) / 0.27577711f;
                }
            }
        });

        return tensor;
    }

    /// <summary>
    /// Loads an image from memory, resizes to 224x224, and applies CLIP normalization.
    /// Returns a float[3, 224, 224] tensor.
    /// </summary>
    private static float[,,] PreprocessImageFromMemory(byte[] data)
    {
        using var image = Image.Load<Rgb24>(new MemoryStream(data));
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(224, 224),
            Mode = ResizeMode.Crop
        }));

        var tensor = new float[3, 224, 224];
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < 224; y++)
            {
                var pixelRow = accessor.GetRowSpan(y);
                for (int x = 0; x < 224; x++)
                {
                    var pixel = pixelRow[x];
                    tensor[0, y, x] = (pixel.R / 255f - 0.48145466f) / 0.26862954f;
                    tensor[1, y, x] = (pixel.G / 255f - 0.4578275f) / 0.26130258f;
                    tensor[2, y, x] = (pixel.B / 255f - 0.40821073f) / 0.27577711f;
                }
            }
        });

        return tensor;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Internal record for the producer-consumer pipeline.
/// </summary>
internal readonly record struct PreparedImageBatch(
    int BatchStart,
    int TotalToProcess,
    List<int> Indices,
    DenseTensor<float>? Tensor);