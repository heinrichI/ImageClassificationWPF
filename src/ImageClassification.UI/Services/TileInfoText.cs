namespace ImageClassification.UI.Services;

using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Formats the info line of a results tile ("1920 × 1080 | 1.4 MB") from the original
/// size reported by the thumbnail pipeline, and a byte count into a human unit (B/KB/MB/GB).
/// </summary>
internal static class TileInfoText
{
    /// <summary>Shown while the source info is not known yet (or could not be read).</summary>
    public const string Fallback = "(click to open)";

    /// <summary>"W × H · size" — parts joined with " · " as they become known; the fallback hint when nothing is known.</summary>
    public static string Format(int width, int height, long byteSize)
    {
        if (width <= 0 && byteSize <= 0) return Fallback;

        var parts = new List<string>(2);
        if (width > 0 && height > 0) parts.Add($"{width} × {height}");
        if (byteSize > 0) parts.Add(FormatSize(byteSize));
        return string.Join(" | ", parts);
    }

    /// <summary>Human-readable byte size ("512 B", "852 KB", "1.4 MB", "1.2 GB"); invariant culture so the decimal separator is always a dot.</summary>
    public static string FormatSize(long bytes)
    {
        const double kb = 1024d;
        if (bytes < kb) return $"{bytes} B";
        if (bytes < kb * kb) return Invariant(bytes / kb, "KB");
        if (bytes < kb * kb * kb) return Invariant(bytes / (kb * kb), "MB");
        return Invariant(bytes / (kb * kb * kb), "GB");
    }

    private static string Invariant(double value, string unit) => $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {unit}";
}