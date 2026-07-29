using ImageClassification.Core.Models;

namespace ImageClassification.Tests.Models;

public class ModelDownloadInfoTests
{
    [Fact]
    public void SizeText_Bytes_ShowsB()
    {
        var model = new ModelDownloadInfo { SizeBytes = 500 };
        Assert.Equal("500 B", model.SizeText);
    }

    [Fact]
    public void SizeText_Kilobytes_ShowsKB()
    {
        var model = new ModelDownloadInfo { SizeBytes = 5_000 };
        Assert.Contains("KB", model.SizeText);
    }

    [Fact]
    public void SizeText_Megabytes_ShowsMB()
    {
        var model = new ModelDownloadInfo { SizeBytes = 5_000_000 };
        Assert.Contains("MB", model.SizeText);
    }

    [Fact]
    public void IsDownloaded_FileNotExists_ReturnsFalse()
    {
        var model = new ModelDownloadInfo
        {
            LocalPath = Path.Combine(Path.GetTempPath(), "nonexistent_model.onnx")
        };

        Assert.False(model.IsDownloaded);
    }

    [Fact]
    public void ModelDownloadInfo_AllPropertiesSet()
    {
        var model = new ModelDownloadInfo
        {
            Name = "Test Model",
            Description = "A test model",
            DownloadUrl = "https://example.com/model.onnx",
            SizeBytes = 1_000_000,
            Filename = "test.onnx",
            LocalPath = "/tmp/test.onnx"
        };

        Assert.Equal("Test Model", model.Name);
        Assert.Equal("A test model", model.Description);
        Assert.Equal("https://example.com/model.onnx", model.DownloadUrl);
        Assert.Equal(1_000_000, model.SizeBytes);
        Assert.Equal("test.onnx", model.Filename);
    }
}
