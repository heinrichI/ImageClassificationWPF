using System.Text;
using ImageClassification.Core.Helpers;
using Xunit;

namespace ImageClassification.Tests.Helpers;

/// <summary>
/// Tests for <see cref="VectorCodec"/> — the text form ("ViT hash") of an embedding
/// vector that the UI copies to the clipboard and pastes back into the search box.
/// </summary>
public class VectorCodecTests
{
    [Fact]
    public void RoundTrip_ProducesTheSameVector()
    {
        var vector = new float[] { 1f, -2.5f, 0f, 0.125f, 3.5f };

        var text = VectorCodec.ToBase64(vector);

        Assert.True(VectorCodec.TryParseBase64(text, out var parsed));
        Assert.Equal(vector, parsed);
    }

    [Fact]
    public void RoundTrip_WorksForA512FloatClipEmbedding()
    {
        var vector = new float[512];
        for (var i = 0; i < vector.Length; i++)
            vector[i] = i % 7 / 7f - 0.5f;

        var text = VectorCodec.ToBase64(vector);

        // A 512-float hash is ~2.7 KB of base64 — clipboard-friendly.
        Assert.True(VectorCodec.TryParseBase64(text, out var parsed));
        Assert.Equal(vector, parsed);
    }

    [Fact]
    public void Parse_IgnoresWhitespaceAndLineBreaks()
    {
        // Clipboard round-trips (and editors) may wrap the long hash onto several lines.
        var vector = new float[64];
        for (var i = 0; i < vector.Length; i++)
            vector[i] = i;

        var text = VectorCodec.ToBase64(vector);
        var sb = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            sb.Append(text[i]);
            if (i % 48 == 47)
                sb.Append('\n');
        }

        Assert.True(VectorCodec.TryParseBase64(sb.ToString(), out var parsed));
        Assert.Equal(vector, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a hash")]          // length % 4 != 0 after whitespace removal
    [InlineData("AB=")]                 // length % 4 != 0
    [InlineData("a hash with (parentheses) and 100% signs")] // invalid base64 characters
    public void Parse_InvalidInput_ReturnsFalse(string? text)
    {
        Assert.False(VectorCodec.TryParseBase64(text, out var vector));
        Assert.Empty(vector);
    }

    [Fact]
    public void ToBase64_NullVector_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => VectorCodec.ToBase64(null!));
    }
}