using ImageClassification.UI.Services;
using Xunit;

namespace ImageClassification.Tests.Services;

/// <summary>
/// Normalization / dedup rules shared by the Comic Search and Search Image directory lists
/// (folder dialog + drag-and-drop): Windows paths are case-insensitive, and a trailing
/// backslash must never turn one directory into a "second" one.
/// </summary>
public class DirectoryPathsTests
{
    [Theory]
    [InlineData("J:\\x", "J:\\x")]
    [InlineData("J:\\x\\", "J:\\x")]
    [InlineData("J:\\x\\\\", "J:\\x")]
    [InlineData("  J:\\x\\ ", "J:\\x")]
    [InlineData("\\\\server\\share\\", "\\\\server\\share")]
    [InlineData("J:", "J:")]
    public void Normalize_TrimsWhitespaceAndTrailingBackslashes(string input, string expected)
        => Assert.Equal(expected, DirectoryPaths.Normalize(input));

    [Fact]
    public void Normalize_NeverEatsTheDriveRootBackslash()
        => Assert.Equal("J:\\", DirectoryPaths.Normalize("J:\\"));

    [Fact]
    public void IsDuplicate_IgnoresCaseAndTrailingBackslash()
    {
        var existing = new[] { "J:\\Comics\\DC" };

        Assert.True(DirectoryPaths.IsDuplicate(existing, "j:\\comics\\dc\\"));
        Assert.True(DirectoryPaths.IsDuplicate(existing, "J:\\Comics\\DC\\"));
        Assert.False(DirectoryPaths.IsDuplicate(existing, "J:\\Comics\\DC\\Sub"));
    }
}