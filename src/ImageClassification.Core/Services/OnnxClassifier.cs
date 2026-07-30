using System.Collections.Concurrent;
using ImageClassification.Core.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageClassification.Core.Services;

internal sealed class OnnxClassifier : IImageClassifier
{
    private readonly IModelDownloader _downloader;
    private InferenceSession? _session;
    private string[] _labels = Array.Empty<string>();
    private int _imageSize = 224;
    private bool _initialized;
    private Task? _initTask;
    private readonly object _initLock = new();

    public ModelInfo Model { get; private set; } = new();

    public OnnxClassifier(IModelDownloader downloader)
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
        var labelsPath = _downloader.GetLabelsPath();
        await LoadModelAsync(modelPath, labelsPath);
        _initialized = true;
    }

    public async Task LoadModelAsync(string modelPath, string labelFilePath)
    {
        Dispose();

        var sessionOptions = new SessionOptions
        {
            InterOpNumThreads = 4,
            IntraOpNumThreads = 4,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        _session = new InferenceSession(modelPath, sessionOptions);

        _labels = await File.ReadAllLinesAsync(labelFilePath);
        _labels = _labels.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

        var inputMeta = _session.InputMetadata.First();
        var dims = inputMeta.Value.Dimensions;
        if (dims.Length >= 4)
        {
            _imageSize = dims[2] <= 0 ? 224 : dims[2];
        }

        Model = new ModelInfo
        {
            ModelPath = modelPath,
            ImageSize = _imageSize,
            NumClasses = _labels.Length,
            ClassLabels = _labels,
            FileSizeBytes = new FileInfo(modelPath).Length,
            TrainedDate = File.GetLastWriteTime(modelPath)
        };

        await Task.CompletedTask;
    }

    public ClassificationResult Classify(string imagePath)
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
        var output = results.First().AsTensor<float>().ToArray();

        var allProbabilities = new Dictionary<string, float>();
        for (int i = 0; i < _labels.Length && i < output.Length; i++)
        {
            allProbabilities[_labels[i]] = output[i];
        }

        var maxIndex = Array.IndexOf(output, output.Max());
        var predictedClass = maxIndex < _labels.Length ? _labels[maxIndex] : "Unknown";
        var confidence = output[maxIndex];

        return new ClassificationResult(imagePath, predictedClass, confidence, allProbabilities);
    }

    public async Task<List<ClassificationResult>> ClassifyBatchAsync(
        List<string> imagePaths, IProgress<int>? progress = null)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        var concurrently = new ConcurrentBag<ClassificationResult>();

        await Task.Run(() =>
        {
            int completed = 0;
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 4 };

            Parallel.ForEach(imagePaths, parallelOptions, imagePath =>
            {
                try
                {
                    var result = Classify(imagePath);
                    concurrently.Add(result);
                }
                catch { /* Skip unprocessable images */ }

                var count = Interlocked.Increment(ref completed);
                progress?.Report(count);
            });
        });

        return concurrently.OrderBy(r => imagePaths.IndexOf(r.FilePath)).ToList();
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        GC.SuppressFinalize(this);
    }
}
