using System.IO.Compression;
using System.Text;
using ImageClassification.Core.Services;

namespace ImageClassification.Tests.Services;

public sealed class ClipBpeTokenizerTests
{
    /// <summary>
    /// Minimal BPE vocab built the same way as the real one:
    /// 256 bytes → 256 byte+&lt;/w&gt; → a few merge strings → 2 specials
    /// </summary>
    private static ClipBpeTokenizer CreateMiniTokenizer()
    {
        var bpePath = Path.GetTempFileName();
        try
        {
            // Build the byte encoder values to know valid merge symbols
            var byteValues = new List<string>();
            for (int i = (int)'!'; i <= (int)'~'; i++) byteValues.Add(char.ConvertFromUtf32(i));
            for (int i = (int)'¡'; i <= (int)'¬'; i++) byteValues.Add(char.ConvertFromUtf32(i));
            for (int i = (int)'®'; i <= (int)'ÿ'; i++) byteValues.Add(char.ConvertFromUtf32(i));
            for (int b = 0; b < 256; b++)
            {
                var ch = char.ConvertFromUtf32(b);
                if (!byteValues.Contains(ch))
                    byteValues.Add(char.ConvertFromUtf32(256 + (b - byteValues.Count)));
            }

            // Write gzip file: header + a few merge pairs using real byte tokens
            using (var fileStream = File.Create(bpePath))
            using (var gzipStream = new GZipStream(fileStream, CompressionMode.Compress))
            using (var writer = new StreamWriter(gzipStream, Encoding.UTF8))
            {
                writer.WriteLine("#version 0.2");
                // Create a few merges using tokens that actually exist in the byte vocab
                // â (byte 226 → 0xE2) and € (byte 128 → 0x80) exist in the byte encoder
                var a = byteValues[226]; // â
                var b = byteValues[128]; // €
                writer.WriteLine($"{a} {b}");
                var c = byteValues[130]; // ‚
                writer.WriteLine($"{a} {c}");
                var d = byteValues[131]; // ƒ
                writer.WriteLine($"{c} {d}");
            }

            return new ClipBpeTokenizer(bpePath);
        }
        finally
        {
            if (File.Exists(bpePath))
                File.Delete(bpePath);
        }
    }

    // ── Unit tests (no external files) ──────────────────────────────────────

    [Fact]
    public void Tokenize_EmptyText_ReturnsSotAndEotOnly()
    {
        // The mini tokenizer won't tokenize arbitrary text correctly,
        // but we can verify the shape contract.
        // For empty string Encode returns empty, so Tokenize should be [SOT, EOT, 0…]
        var tokenizer = CreateMiniTokenizer();

        var result = tokenizer.Tokenize("");

        Assert.Equal(77, result.Length);
        Assert.Equal(ClipBpeTokenizer.StartToken, result[0]);
        Assert.Equal(ClipBpeTokenizer.EndToken, result[1]);
        for (int i = 2; i < 77; i++)
            Assert.Equal(0, result[i]);
    }

    [Fact]
    public void MaxTextTokens_Is77()
    {
        Assert.Equal(77, ClipBpeTokenizer.MaxTextTokens);
    }

    [Fact]
    public void TokenIds_SotEot_AreCorrect()
    {
        Assert.Equal(49406, ClipBpeTokenizer.StartToken);
        Assert.Equal(49407, ClipBpeTokenizer.EndToken);
    }

    // ── Golden token tests (require real BPE file) ─────────────────────────

    private static string? FindBpeFile()
    {
        // Search common model locations
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "models", "bpe_simple_vocab_16e6.txt.gz"),
            Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..",
                "src", "ImageClassification.Core", "bin", "Debug", "net8.0", "models",
                "bpe_simple_vocab_16e6.txt.gz"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static ClipBpeTokenizer? TryCreateRealTokenizer()
    {
        var path = FindBpeFile();
        return path is not null ? new ClipBpeTokenizer(path) : null;
    }

    [Fact]
    public void Tokenize_beach_ReturnsGoldenIds()
    {
        var tokenizer = TryCreateRealTokenizer();
        if (tokenizer is null)
            return; // Skip — no real BPE file available

        var tokens = tokenizer.Tokenize("beach");

        Assert.Equal(ClipBpeTokenizer.StartToken, tokens[0]);
        Assert.Equal(2117, tokens[1]);
        Assert.Equal(ClipBpeTokenizer.EndToken, tokens[2]);
        for (int i = 3; i < 77; i++)
            Assert.Equal(0, tokens[i]);
    }

    [Fact]
    public void Tokenize_summer_ReturnsGoldenIds()
    {
        var tokenizer = TryCreateRealTokenizer();
        if (tokenizer is null)
            return;

        var tokens = tokenizer.Tokenize("summer");

        Assert.Equal(ClipBpeTokenizer.StartToken, tokens[0]);
        Assert.Equal(1673, tokens[1]);
        Assert.Equal(ClipBpeTokenizer.EndToken, tokens[2]);
        for (int i = 3; i < 77; i++)
            Assert.Equal(0, tokens[i]);
    }

    [Fact]
    public void Tokenize_APhotoOfABeach_ReturnsGoldenIds()
    {
        var tokenizer = TryCreateRealTokenizer();
        if (tokenizer is null)
            return;

        var tokens = tokenizer.Tokenize("a photo of a beach");

        Assert.Equal(ClipBpeTokenizer.StartToken, tokens[0]);
        Assert.Equal(320, tokens[1]);   // "a"
        Assert.Equal(1125, tokens[2]);  // "photo"
        Assert.Equal(539, tokens[3]);   // "of"
        Assert.Equal(320, tokens[4]);   // "a"
        Assert.Equal(2117, tokens[5]);  // "beach"
        Assert.Equal(ClipBpeTokenizer.EndToken, tokens[6]);
        for (int i = 7; i < 77; i++)
            Assert.Equal(0, tokens[i]);
    }

    [Fact]
    public void Encode_ReturnsRawIdsWithoutSotEot()
    {
        var tokenizer = TryCreateRealTokenizer();
        if (tokenizer is null)
            return;

        var rawIds = tokenizer.Encode("beach");

        // Should not start with SOT
        Assert.DoesNotContain((int)ClipBpeTokenizer.StartToken, rawIds);
        Assert.Contains(2117, rawIds);
    }

    [Fact]
    public void Tokenize_VeryLongText_TruncatesToMaxTokens()
    {
        var tokenizer = TryCreateRealTokenizer();
        if (tokenizer is null)
            return;

        // Generate enough words to exceed 77 tokens
        var words = string.Join(" ", Enumerable.Repeat("beach summer photo cat dog", 50));
        var tokens = tokenizer.Tokenize(words);

        Assert.Equal(77, tokens.Length);
        Assert.Equal(ClipBpeTokenizer.StartToken, tokens[0]);
        Assert.Equal(ClipBpeTokenizer.EndToken, tokens[^1]);
    }
}
