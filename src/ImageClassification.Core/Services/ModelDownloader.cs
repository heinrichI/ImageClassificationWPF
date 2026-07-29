using System.IO;
using System.Net.Http;
using ImageClassification.Core.Models;

namespace ImageClassification.Core.Services;

public class ModelDownloader : IModelDownloader
{
    private readonly HttpClient _httpClient;
    private readonly List<ModelDownloadInfo> _models;

    public string ModelsDirectory { get; }

    public ModelDownloader()
    {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(30);

        ModelsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ImageClassification", "models");

        Directory.CreateDirectory(ModelsDirectory);

        _models = CreateDefaultModelList();
        RefreshDownloadStatus();
    }

    public List<ModelDownloadInfo> GetAvailableModels() => _models;

    public void RefreshDownloadStatus()
    {
        foreach (var model in _models)
        {
            model.LocalPath = Path.Combine(ModelsDirectory, model.Filename);
        }
    }

    public async Task DownloadModelAsync(ModelDownloadInfo model, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (model.IsDownloaded) return;

        model.LocalPath = Path.Combine(ModelsDirectory, model.Filename);
        var tempPath = model.LocalPath + ".downloading";

        try
        {
            using var response = await _httpClient.GetAsync(model.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? model.SizeBytes;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    progress?.Report((int)(totalRead * 100 / totalBytes));
                }
            }
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }

        // Move temp file to final location
        if (File.Exists(model.LocalPath))
            File.Delete(model.LocalPath);
        File.Move(tempPath, model.LocalPath);
    }

    private List<ModelDownloadInfo> CreateDefaultModelList()
    {
        return new List<ModelDownloadInfo>
        {
            new()
            {
                Name = "EfficientNet-B0 (ONNX)",
                Description = "78.57% Top-1, 5.3M params, 224x224. Best accuracy/size ratio.",
                DownloadUrl = "https://github.com/onnx/models/raw/main/validated/vision/classification/efficientnet-lite4/model/efficientnet-lite4.onnx",
                SizeBytes = 75_000_000,
                Filename = "efficientnet_b0.onnx"
            },
            new()
            {
                Name = "MobileNetV2 (ONNX)",
                Description = "72.91% Top-1, 3.5M params, 224x224. Lightweight and fast.",
                DownloadUrl = "https://github.com/onnx/models/raw/main/validated/vision/classification/mobilenet/model/mobilenetv2-12.onnx",
                SizeBytes = 14_000_000,
                Filename = "mobilenet_v2.onnx"
            },
            new()
            {
                Name = "CLIP ViT-B/32",
                Description = "Zero-shot image tagging. ~350 MB. Required for 'By Tags' mode.",
                DownloadUrl = "https://huggingface.co/openai/clip-vit-base-patch32/resolve/main/onnx/model.onnx",
                SizeBytes = 350_000_000,
                Filename = "clip_vit_b32.onnx"
            }
        };
    }
}
