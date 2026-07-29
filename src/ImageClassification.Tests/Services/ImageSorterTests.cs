using ImageClassification.Core.Services;
using Moq;

namespace ImageClassification.Tests.Services;

public class ImageSorterTests
{
    private readonly Mock<IImageClassifier> _mockClassifier;

    public ImageSorterTests()
    {
        _mockClassifier = new Mock<IImageClassifier>();
    }

    [Fact]
    public async Task SortImagesAsync_EmptyDirectory_ReturnsZeroSorted()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        var sorter = new ImageSorter(_mockClassifier.Object);

        try
        {
            // Act
            var result = await sorter.SortImagesAsync(
                "model.onnx", "labels.txt", tempDir,
                0.9f, 224, 32);

            // Assert
            Assert.Equal(0, result.TotalImages);
            Assert.Equal(0, result.SortedImages);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SortProgress_PropertiesAreSet()
    {
        var progress = new SortProgress
        {
            Current = 5,
            Total = 10,
            CurrentFile = "test.jpg",
            PredictedClass = "cat",
            Confidence = 0.95f
        };

        Assert.Equal(5, progress.Current);
        Assert.Equal(10, progress.Total);
        Assert.Equal("test.jpg", progress.CurrentFile);
        Assert.Equal("cat", progress.PredictedClass);
        Assert.Equal(0.95f, progress.Confidence);
    }

    [Fact]
    public void SortResult_DefaultValues()
    {
        var result = new SortResult();

        Assert.Equal(0, result.TotalImages);
        Assert.Equal(0, result.SortedImages);
        Assert.Equal(0, result.SkippedImages);
        Assert.Empty(result.FilesPerClass);
    }

    [Fact]
    public void SortResult_FilesPerClass_CountsCorrectly()
    {
        var result = new SortResult
        {
            FilesPerClass = new Dictionary<string, int>
            {
                ["cat"] = 10,
                ["dog"] = 5,
                ["bird"] = 3
            }
        };

        Assert.Equal(10, result.FilesPerClass["cat"]);
        Assert.Equal(5, result.FilesPerClass["dog"]);
        Assert.Equal(3, result.FilesPerClass["bird"]);
        Assert.Equal(3, result.FilesPerClass.Count);
    }
}
