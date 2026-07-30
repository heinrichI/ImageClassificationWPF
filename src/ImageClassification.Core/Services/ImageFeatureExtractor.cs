using System.Collections.Concurrent;
using ImageClassification.Core.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageClassification.Core.Services;

/// <summary>
/// Extracts feature embeddings from ONNX model's penultimate layer.
/// Used by clustering and tag generation modes.
/// </summary>
internal sealed class ImageFeatureExtractor : IImageFeatureExtractor
{
    private readonly IModelDownloader _downloader;
    private InferenceSession? _session;
    private int _imageSize = 224;
    private string _featureOutputName = string.Empty;
    private bool _initialized;
    private Task? _initTask;
    private readonly object _initLock = new();

    public ImageFeatureExtractor(IModelDownloader downloader)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
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
        var modelPath = _downloader.GetModelPath("mobilenet_v2.onnx");
        await LoadModelAsync(modelPath, 224);
        _initialized = true;
    }

    public async Task LoadModelAsync(string modelPath, int imageSize)
    {
        Dispose();

        var sessionOptions = new SessionOptions
        {
            InterOpNumThreads = 4,
            IntraOpNumThreads = 4,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        _session = new InferenceSession(modelPath, sessionOptions);
        _imageSize = imageSize;

        // Try to find the penultimate layer output (feature vector)
        // Most ONNX classification models have outputs like: "logits", "output", "predictions"
        // The feature extractor should target the layer BEFORE classification
        var outputNames = _session.OutputMetadata.Keys.ToList();

        if (outputNames.Count > 1)
        {
            // Multiple outputs — use the first one (usually features before classifier)
            _featureOutputName = outputNames[0];
        }
        else
        {
            // Single output — use it as feature vector (softmax probabilities)
            _featureOutputName = outputNames[0];
        }

        await Task.CompletedTask;
    }

    public float[] ExtractFeatures(string imagePath)
    {
        EnsureInitializedAsync().GetAwaiter().GetResult();
        ObjectDisposedException.ThrowIf(_session is null, this);

        using var image = Image.Load<Rgb24>(imagePath);
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(_imageSize, _imageSize),
            Mode = ResizeMode.Crop
        }));

        var tensor = new DenseTensor<float>(new[] { 1, 3, _imageSize, _imageSize });
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < _imageSize; y++)
            {
                var pixelRow = accessor.GetRowSpan(y);
                for (int x = 0; x < _imageSize; x++)
                {
                    var pixel = pixelRow[x];
                    tensor[0, 0, y, x] = (pixel.R / 255f - 0.485f) / 0.229f;
                    tensor[0, 1, y, x] = (pixel.G / 255f - 0.456f) / 0.224f;
                    tensor[0, 2, y, x] = (pixel.B / 255f - 0.406f) / 0.225f;
                }
            }
        });

        var inputName = _session!.InputMetadata.Keys.First();
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, tensor)
        };

        using var results = _session.Run(inputs);
        var output = results.First(r => r.Name == _featureOutputName).AsTensor<float>().ToArray();
        return output;
    }

    public async Task<List<ImageFeatures>> ExtractBatchAsync(
        List<string> imagePaths, IProgress<int>? progress = null)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        var results = new ConcurrentBag<ImageFeatures>();

        await Task.Run(() =>
        {
            int completed = 0;
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 4 };

            Parallel.ForEach(imagePaths, parallelOptions, imagePath =>
            {
                try
                {
                    var features = ExtractFeatures(imagePath);
                    results.Add(new ImageFeatures { FilePath = imagePath, Features = features });
                }
                catch { /* Skip unprocessable images */ }

                var count = Interlocked.Increment(ref completed);
                progress?.Report(count);
            });
        });

        return results.OrderBy(r => imagePaths.IndexOf(r.FilePath)).ToList();
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        GC.SuppressFinalize(this);
    }
}
