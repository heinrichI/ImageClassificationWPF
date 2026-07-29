using ImageClassification.Core.Models;

namespace ImageClassification.Tests.Models;

public class ModelArchitectureTests
{
    [Theory]
    [InlineData(ModelArchitecture.EfficientNetB0, 224)]
    [InlineData(ModelArchitecture.EfficientNetB1, 224)]
    [InlineData(ModelArchitecture.EfficientNetV2S, 384)]
    [InlineData(ModelArchitecture.ConvNeXtTiny, 224)]
    [InlineData(ModelArchitecture.MobileNetV3Large, 224)]
    public void GetDefaultImageSize_ReturnsCorrectSize(ModelArchitecture arch, int expectedSize)
    {
        Assert.Equal(expectedSize, arch.GetDefaultImageSize());
    }

    [Theory]
    [InlineData(ModelArchitecture.EfficientNetB0, "EfficientNet-B0")]
    [InlineData(ModelArchitecture.EfficientNetB1, "EfficientNet-B1")]
    [InlineData(ModelArchitecture.EfficientNetV2S, "EfficientNetV2-S")]
    [InlineData(ModelArchitecture.ConvNeXtTiny, "ConvNeXt-Tiny")]
    [InlineData(ModelArchitecture.MobileNetV3Large, "MobileNetV3-Large")]
    public void GetDisplayName_ReturnsCorrectName(ModelArchitecture arch, string expectedName)
    {
        Assert.Equal(expectedName, arch.GetDisplayName());
    }

    [Theory]
    [InlineData(ModelArchitecture.EfficientNetB0, 5)]
    [InlineData(ModelArchitecture.EfficientNetB1, 8)]
    [InlineData(ModelArchitecture.EfficientNetV2S, 21)]
    [InlineData(ModelArchitecture.ConvNeXtTiny, 29)]
    [InlineData(ModelArchitecture.MobileNetV3Large, 6)]
    public void GetParameterCountMillions_ReturnsApproximateCount(ModelArchitecture arch, int expectedMin)
    {
        var count = arch.GetParameterCountMillions();
        Assert.True(count >= expectedMin - 1 && count <= expectedMin + 1,
            $"Expected ~{expectedMin}M but got {count}M for {arch}");
    }
}
