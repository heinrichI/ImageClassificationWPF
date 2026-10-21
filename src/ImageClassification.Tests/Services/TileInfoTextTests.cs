using ImageClassification.UI.Services;
using Xunit;

namespace ImageClassification.Tests.Services;

/// <summary>
/// The results-tile info line ("1920 × 1080 · 1.4 MB") and the byte-size human units.
/// </summary>
public class TileInfoTextTests
{
    [Theory]
    [InlineData(0, 0, 0, "(click to open)")]
    [InlineData(1920, 1080, 0, "1920 × 1080")]
    [InlineData(0, 0, 1468006, "1.4 MB")]
    [InlineData(1920, 1080, 1468006, "1920 × 1080 | 1.4 MB")]
    public void Format_CombinesKnownParts(int width, int height, long byteSize, string expected)
        => Assert.Equal(expected, TileInfoText.Format(width, height, byteSize));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(872448, "852 KB")]     // 852 * 1024
    [InlineData(1468006, "1.4 MB")]    // 1.4 * 1024^2
    [InlineData(1288490188, "1.2 GB")] // 1.2 * 1024^3
    public void FormatSize_HumanUnits(long bytes, string expected)
        => Assert.Equal(expected, TileInfoText.FormatSize(bytes));
}