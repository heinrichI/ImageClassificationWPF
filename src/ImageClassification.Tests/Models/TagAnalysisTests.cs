using ImageClassification.Core.Models;

namespace ImageClassification.Tests.Models;

public class TagAnalysisTests
{
    [Fact]
    public void ImageTagResult_DefaultValues()
    {
        var result = new ImageTagResult();

        Assert.Equal(string.Empty, result.FilePath);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public void ImageTagResult_CanSetProperties()
    {
        var result = new ImageTagResult
        {
            FilePath = "/path/photo.jpg",
            Tags = new List<(string Tag, float Score)>
            {
                ("cat", 0.9f),
                ("animal", 0.85f),
                ("pet", 0.7f)
            }
        };

        Assert.Equal("/path/photo.jpg", result.FilePath);
        Assert.Equal(3, result.Tags.Count);
        Assert.Equal("cat", result.Tags[0].Tag);
        Assert.Equal(0.9f, result.Tags[0].Score);
    }

    [Fact]
    public void TagAnalysisResult_DefaultValues()
    {
        var result = new TagAnalysisResult();

        Assert.Empty(result.ImageTags);
        Assert.Empty(result.TagCounts);
        Assert.Equal(string.Empty, result.SourceDirectory);
    }

    [Fact]
    public void TagAnalysisResult_CanTrackTagCounts()
    {
        var result = new TagAnalysisResult
        {
            TagCounts = new Dictionary<string, int>
            {
                ["cat"] = 10,
                ["dog"] = 5,
                ["landscape"] = 20
            }
        };

        Assert.Equal(10, result.TagCounts["cat"]);
        Assert.Equal(5, result.TagCounts["dog"]);
        Assert.Equal(20, result.TagCounts["landscape"]);
    }
}
