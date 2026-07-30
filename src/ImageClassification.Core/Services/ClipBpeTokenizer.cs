using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ImageClassification.Core.Services;

/// <summary>
/// OpenAI CLIP <c>SimpleTokenizer</c> port — byte-level BPE matching
/// <c>clip.simple_tokenizer</c> exactly (vocab size 49408, SOT=49406, EOT=49407).
/// </summary>
internal sealed partial class ClipBpeTokenizer
{
    public const int MaxTextTokens = 77;
    public const long StartToken = 49406L; // <|startoftext|>
    public const long EndToken = 49407L;   // <|endoftext|>

    // Matches OpenAI CLIP pat (without requiring the Python regex module):
    // specials | contractions | letters | numbers | other non-space
    [GeneratedRegex(
        @"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ClipTokenPattern();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespacePattern();

    private readonly Dictionary<string, int> _encoder;
    private readonly Dictionary<(string, string), int> _bpeRanks;
    private readonly Dictionary<byte, string> _byteEncoder;
    private readonly Dictionary<string, string> _cache;

    public ClipBpeTokenizer(string bpePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bpePath);
        if (!File.Exists(bpePath))
            throw new FileNotFoundException("CLIP BPE vocabulary file not found.", bpePath);

        _byteEncoder = BuildByteEncoder();
        _cache = new Dictionary<string, string>
        {
            ["<|startoftext|>"] = "<|startoftext|>",
            ["<|endoftext|>"] = "<|endoftext|>"
        };

        // OpenAI: merges = gzip lines; merges = merges[1:49152-256-2+1]
        var allLines = ReadAllLinesGzip(bpePath);
        int mergeCount = 49152 - 256 - 2; // 48894
        var mergeLines = allLines
            .Skip(1) // version header
            .Take(mergeCount)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var merges = new List<(string A, string B)>(mergeLines.Count);
        foreach (var line in mergeLines)
        {
            var parts = line.Split(' ');
            if (parts.Length == 2)
                merges.Add((parts[0], parts[1]));
        }

        // vocab = bytes + byte+</w> + joined merges + specials
        var vocab = new List<string>(256 + 256 + merges.Count + 2);
        var byteValues = _byteEncoder.OrderBy(kv => GetByteOrderKey(kv.Key, kv.Value))
            .Select(kv => kv.Value)
            .ToList();

        // Order must match bytes_to_unicode() value order, not sorted by byte.
        // OpenAI builds: list(bytes_to_unicode().values()) which follows insertion order:
        // printable ranges first, then specials remapped to 256+n.
        byteValues = BuildByteEncoderValuesInOpenAiOrder();

        vocab.AddRange(byteValues);
        vocab.AddRange(byteValues.Select(v => v + "</w>"));
        foreach (var (a, b) in merges)
            vocab.Add(a + b);
        vocab.Add("<|startoftext|>");
        vocab.Add("<|endoftext|>");

        _encoder = new Dictionary<string, int>(vocab.Count);
        for (int i = 0; i < vocab.Count; i++)
            _encoder[vocab[i]] = i;

        _bpeRanks = new Dictionary<(string, string), int>(merges.Count);
        for (int i = 0; i < merges.Count; i++)
            _bpeRanks[merges[i]] = i;
    }

    /// <summary>
    /// Tokenizes text to a fixed-length 77-token CLIP sequence:
    /// <c>[SOT] + bpe_ids + [EOT] + pad0…</c>
    /// </summary>
    public long[] Tokenize(string text)
    {
        var bpeTokenIds = Encode(text);

        var tokenIds = new long[MaxTextTokens];
        tokenIds[0] = StartToken;

        int write = 1;
        foreach (var id in bpeTokenIds)
        {
            if (write >= MaxTextTokens - 1)
                break;
            tokenIds[write++] = id;
        }

        tokenIds[write] = EndToken;
        // remaining slots already 0 (pad)
        return tokenIds;
    }

    /// <summary>
    /// Returns raw BPE token IDs without SOT/EOT/padding (OpenAI <c>encode</c>).
    /// </summary>
    public IReadOnlyList<int> Encode(string text)
    {
        text = WhitespaceClean(BasicClean(text)).ToLowerInvariant();

        var bpeTokens = new List<int>();
        foreach (Match match in ClipTokenPattern().Matches(text))
        {
            var token = match.Value;
            var byteEncoded = string.Concat(
                Encoding.UTF8.GetBytes(token).Select(b => _byteEncoder[b]));

            var bpe = Bpe(byteEncoded);
            foreach (var piece in bpe.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (_encoder.TryGetValue(piece, out int id))
                    bpeTokens.Add(id);
            }
        }

        return bpeTokens;
    }

    private string Bpe(string token)
    {
        if (_cache.TryGetValue(token, out var cached))
            return cached;

        if (token.Length == 0)
            return token;

        // word = tuple(token[:-1]) + (token[-1] + '</w>',)
        var word = new List<string>(token.Length);
        for (int i = 0; i < token.Length - 1; i++)
            word.Add(token[i].ToString());
        word.Add(token[^1] + "</w>");

        var pairs = GetPairs(word);
        if (pairs.Count == 0)
        {
            var result = token + "</w>";
            _cache[token] = result;
            return result;
        }

        while (true)
        {
            // bigram with lowest rank
            (string A, string B)? best = null;
            int bestRank = int.MaxValue;
            foreach (var pair in pairs)
            {
                if (_bpeRanks.TryGetValue(pair, out int rank) && rank < bestRank)
                {
                    bestRank = rank;
                    best = pair;
                }
            }

            if (best is null)
                break;

            var (first, second) = best.Value;
            var newWord = new List<string>();
            int i = 0;
            while (i < word.Count)
            {
                int j = IndexOf(word, first, i);
                if (j < 0)
                {
                    newWord.AddRange(word.Skip(i));
                    break;
                }

                newWord.AddRange(word.Skip(i).Take(j - i));
                i = j;

                if (word[i] == first && i < word.Count - 1 && word[i + 1] == second)
                {
                    newWord.Add(first + second);
                    i += 2;
                }
                else
                {
                    newWord.Add(word[i]);
                    i += 1;
                }
            }

            word = newWord;
            if (word.Count == 1)
                break;

            pairs = GetPairs(word);
        }

        var merged = string.Join(' ', word);
        _cache[token] = merged;
        return merged;
    }

    private static int IndexOf(List<string> word, string value, int start)
    {
        for (int i = start; i < word.Count; i++)
        {
            if (word[i] == value)
                return i;
        }
        return -1;
    }

    private static HashSet<(string, string)> GetPairs(List<string> word)
    {
        var pairs = new HashSet<(string, string)>();
        if (word.Count < 2)
            return pairs;

        string prev = word[0];
        for (int i = 1; i < word.Count; i++)
        {
            pairs.Add((prev, word[i]));
            prev = word[i];
        }
        return pairs;
    }

    private static string BasicClean(string text)
    {
        // OpenAI also runs ftfy.fix_text; for comic queries HTML unescape + strip is enough.
        text = WebUtility.HtmlDecode(WebUtility.HtmlDecode(text));
        return text.Trim();
    }

    private static string WhitespaceClean(string text)
    {
        return WhitespacePattern().Replace(text, " ").Trim();
    }

    private static List<string> ReadAllLinesGzip(string bpePath)
    {
        var lines = new List<string>();
        using var fileStream = File.OpenRead(bpePath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzipStream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            lines.Add(line);
        return lines;
    }

    /// <summary>
    /// OpenAI <c>bytes_to_unicode</c> — insertion order of values is significant for vocab IDs.
    /// </summary>
    private static Dictionary<byte, string> BuildByteEncoder()
    {
        var bs = new List<int>();
        for (int i = (int)'!'; i <= (int)'~'; i++) bs.Add(i);
        for (int i = (int)'¡'; i <= (int)'¬'; i++) bs.Add(i);
        for (int i = (int)'®'; i <= (int)'ÿ'; i++) bs.Add(i);

        var cs = new List<int>(bs);
        int n = 0;
        for (int b = 0; b < 256; b++)
        {
            if (!bs.Contains(b))
            {
                bs.Add(b);
                cs.Add(256 + n);
                n++;
            }
        }

        var encoder = new Dictionary<byte, string>(256);
        for (int i = 0; i < bs.Count; i++)
            encoder[(byte)bs[i]] = char.ConvertFromUtf32(cs[i]);

        return encoder;
    }

    /// <summary>
    /// Values of <c>bytes_to_unicode()</c> in OpenAI insertion order (dict value list order).
    /// </summary>
    private static List<string> BuildByteEncoderValuesInOpenAiOrder()
    {
        var encoder = BuildByteEncoder();
        // Reconstruct the same insertion order as BuildByteEncoder's bs list
        var bs = new List<int>();
        for (int i = (int)'!'; i <= (int)'~'; i++) bs.Add(i);
        for (int i = (int)'¡'; i <= (int)'¬'; i++) bs.Add(i);
        for (int i = (int)'®'; i <= (int)'ÿ'; i++) bs.Add(i);
        for (int b = 0; b < 256; b++)
        {
            if (!bs.Contains(b))
                bs.Add(b);
        }

        return bs.Select(b => encoder[(byte)b]).ToList();
    }

    // Helper only used in an earlier draft path; keep private to avoid analyzer noise if unused.
    private static int GetByteOrderKey(byte b, string _) => b;
}
