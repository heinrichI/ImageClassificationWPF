using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ImageClassification.Core.Services;

/// <summary>
/// CLIP text encoder — loads clip_vit_b32.onnx and runs ONNX inference
/// with proper byte-level BPE tokenization matching OpenAI's CLIP tokenizer.
/// Produces 512-dim L2-normalized text embeddings in the same semantic space
/// as ClipImageEncoder's image embeddings.
/// </summary>
internal sealed class ClipTextEncoder : IClipTextEncoder
{
    private const int EmbeddingDim = 512;

    private readonly IModelDownloader _downloader;
    private readonly ILogger<BatchImageEncoder> _logger;
    private readonly IOnnxRuntimeOptions _onnxRuntimeOptions;
    private ClipBpeTokenizer? _tokenizer;
    private InferenceSession? _session;
    private bool _initialized;
    private Task? _initTask;
    private readonly object _initLock = new();

    public ClipTextEncoder(IModelDownloader downloader, ILogger<BatchImageEncoder> logger, IOnnxRuntimeOptions onnxRuntimeOptions)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _logger = logger;
        _onnxRuntimeOptions = onnxRuntimeOptions ?? throw new ArgumentNullException(nameof(onnxRuntimeOptions));
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
        var modelPath = _downloader.GetModelPath("clip_vit_b32.onnx");
        var bpePath = _downloader.GetModelPath("bpe_simple_vocab_16e6.txt.gz");
        await Task.WhenAll(
            LoadSessionAsync(modelPath),
            LoadBpeVocabularyAsync(bpePath));
        _initialized = true;
    }

    private async Task LoadSessionAsync(string modelPath)
    {
        _session?.Dispose();

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        OnnxSessionOptionsHelper.ApplyThreads(sessionOptions, _onnxRuntimeOptions.ThreadCount);

        try
        {
            sessionOptions.AppendExecutionProvider_CUDA();
        }
        catch(Exception ex)
        {
            // CUDA not available — fall back to CPU
            _logger.LogWarning($"ClipTextEncoder: CUDA unavailable, falling back to CPU {ex.Message}");
        }

        _session = new InferenceSession(modelPath, sessionOptions);
        await Task.CompletedTask;
    }

    private async Task LoadBpeVocabularyAsync(string bpePath)
    {
        _tokenizer = new ClipBpeTokenizer(bpePath);
        await Task.CompletedTask;
    }

    public async Task<float[]> EncodeTextAsync(string text, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_session is null, this);

        // Tokenize text using CLIP's BPE tokenizer
        var tokenIds = _tokenizer!.Tokenize(text);

        // Create input tensor
        var inputIds = new DenseTensor<long>(new[] { 1, ClipBpeTokenizer.MaxTextTokens });
        for (int i = 0; i < ClipBpeTokenizer.MaxTextTokens; i++)
        {
            inputIds[0, i] = tokenIds[i];
        }

        // Create dummy image input (all zeros) — the model requires both inputs
        var dummyImage = new DenseTensor<float>(new[] { 1, 3, 224, 224 });

        var inputMeta = _session!.InputMetadata;
        var textInputName = inputMeta.First(kvp => kvp.Value.ElementDataType == TensorElementType.Int64).Key;
        var imageInputName = inputMeta.First(kvp => kvp.Value.ElementDataType == TensorElementType.Float).Key;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(textInputName, inputIds),
            NamedOnnxValue.CreateFromTensor(imageInputName, dummyImage)
        };

        // Add attention mask if the model expects it
        var attentionMaskInput = inputMeta
            .FirstOrDefault(kvp => kvp.Key.Contains("attention_mask", StringComparison.OrdinalIgnoreCase));
        if (attentionMaskInput.Key is not null)
        {
            var attentionMask = new DenseTensor<long>(new[] { 1, ClipBpeTokenizer.MaxTextTokens });
            for (int i = 0; i < ClipBpeTokenizer.MaxTextTokens; i++)
                attentionMask[0, i] = tokenIds[i] > 0 ? 1L : 0L;
            inputs.Add(NamedOnnxValue.CreateFromTensor(attentionMaskInput.Key, attentionMask));
        }

        using var results = _session.Run(inputs);

        // Read the text embedding output by name
        var embedding = results.First(r => r.Name == "text_embeds").AsTensor<float>().ToArray();

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