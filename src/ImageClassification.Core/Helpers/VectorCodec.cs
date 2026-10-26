using System.Text;

namespace ImageClassification.Core.Helpers;

/// <summary>
/// Text form of an embedding vector — the "ViT hash" the UI copies to the
/// clipboard (right-click a result tile → "Copy hash") and pastes back into the
/// search-by-hash box: the base64 of the raw little-endian float32 array. A
/// 512-float CLIP embedding becomes a ~2.7 KB single-line string.
/// Little-endian is the byte order of every platform .NET supports, so hashes
/// are portable without a header.
/// </summary>
public static class VectorCodec
{
    /// <summary>Encodes the vector as a base64 string of its raw float32 bytes.</summary>
    public static string ToBase64(float[] vector)
    {
        if (vector is null)
            throw new ArgumentNullException(nameof(vector));

        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
            Buffer.BlockCopy(vector, i * sizeof(float), bytes, i * sizeof(float), sizeof(float));
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Parses a base64 hash produced by <see cref="ToBase64"/>. Clipboard round-trips
    /// may add whitespace or line breaks — those are dropped; any other character
    /// (or a byte count that is not a multiple of 4) makes the text an invalid hash.
    /// </summary>
    public static bool TryParseBase64(string? text, out float[] vector)
    {
        vector = [];
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
                continue;
            if (!IsBase64Char(ch))
                return false; // not a hash at all (a phrase, a path, ...)
            sb.Append(ch);
        }

        if (sb.Length == 0 || sb.Length % 4 != 0)
            return false;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(sb.ToString());
        }
        catch (FormatException)
        {
            return false; // valid alphabet but broken padding/structure
        }

        if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0)
            return false;

        vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
            Buffer.BlockCopy(bytes, i * sizeof(float), vector, i * sizeof(float), sizeof(float));
        return true;
    }

    private static bool IsBase64Char(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
           || c is '+' or '/' or '=';
}