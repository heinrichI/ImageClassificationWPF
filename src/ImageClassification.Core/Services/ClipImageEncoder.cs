using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageClassification.Core.Services;

/// <summary>
/// Real CLIP image encoder — loads clip_vit_b32.onnx and runs ONNX inference.
/// Extracted from ComicCoverSearchService so both ComicCoverSearch and ClipTag
/// share a single InferenceSession and a single cache key ("ClipViTB32").
/// </summary>
internal sealed class ClipImageEncoder : IClipImageEncoder
{
    private const int EmbeddingDim = 512;

    private readonly IModelDownloader _downloader;
    private InferenceSession? _session;
    private int _imageSize = 224;
    private bool _initialized;
    private Task? _initTask;
    private readonly object _initLock = new();

    public ClipImageEncoder(IModelDownloader downloader)
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
        var path = _downloader.GetModelPath("clip_vit_b32.onnx");
        await LoadSessionAsync(path);
        _initialized = true;
    }

    private async Task LoadSessionAsync(string modelPath)
    {
        _session?.Dispose();

        var sessionOptions = new SessionOptions
        {
            InterOpNumThreads = 4,
            IntraOpNumThreads = 4,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        _session = new InferenceSession(modelPath, sessionOptions);

        var inputMeta = _session.InputMetadata.First();
        var dims = inputMeta.Value.Dimensions;
        if (dims.Length >= 4 && dims[2] > 0)
            _imageSize = dims[2];

        await Task.CompletedTask;
    }

    public async Task<float[]> EncodeImageAsync(string imagePath, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_session is null, this);

        using var image = Image.Load<Rgb24>(imagePath);
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(_imageSize, _imageSize),
            Mode = ResizeMode.Crop
        }));

        var pixelTensor = new DenseTensor<float>(new[] { 1, 3, _imageSize, _imageSize });
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < _imageSize; y++)
            {
                var pixelRow = accessor.GetRowSpan(y);
                for (int x = 0; x < _imageSize; x++)
                {
                    var pixel = pixelRow[x];
                    pixelTensor[0, 0, y, x] = (pixel.R / 255f - 0.48145466f) / 0.26862954f;
                    pixelTensor[0, 1, y, x] = (pixel.G / 255f - 0.4578275f) / 0.26130258f;
                    pixelTensor[0, 2, y, x] = (pixel.B / 255f - 0.40821073f) / 0.27577711f;
                }
            }
        });

        var inputMeta = _session!.InputMetadata;
        var textInputName = inputMeta.First(kvp => kvp.Value.ElementDataType == TensorElementType.Int64).Key;
        var imageInputName = inputMeta.First(kvp => kvp.Value.ElementDataType == TensorElementType.Float).Key;

        // Dummy text input — all padding tokens
        var textInput = new DenseTensor<long>(new[] { 1, 77 });
        for (int i = 0; i < 77; i++)
            textInput[0, i] = 49407L;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(textInputName, textInput),
            NamedOnnxValue.CreateFromTensor(imageInputName, pixelTensor)
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

        var embedding = results.First(r => r.Name == "image_embeds").AsTensor<float>().ToArray();

        return L2Normalize(embedding);
    }

    private static float[] L2Normalize(float[] vector)
    {
        float norm = 0;
        for (int i = 0; i < vector.Length; i++)
            norm += vector[i] * vector[i];
        norm = MathF.Sqrt(norm);
        if (norm > 0)
            for (int i = 0; i < vector.Length; i++)
                vector[i] /= norm;
        return vector;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        GC.SuppressFinalize(this);
    }
}
