using System.Collections.Generic;

namespace ImageClassification.UI.Services;

/// <summary>
/// Normalization / dedup helpers for the directory-list UI (Comic Search, Search Image).
/// Windows paths are case-insensitive, and the folder dialog as well as drag-and-drop
/// can produce differently-cased or trailing-backslash variants ("J:\x\", "j:\x") —
/// all list comparisons go through <see cref="Normalize"/>.
/// </summary>
internal static class DirectoryPaths
{
    /// <summary>
    /// Trims surrounding whitespace and trailing backslashes — never the drive-root
    /// backslash ("J:\") and never a UNC tail ("\\server\share").
    /// </summary>
    public static string Normalize(string path)
    {
        var result = path.Trim();
        while (result.Length > 3 && result.EndsWith('\\'))
            result = result[..^1];

        return result;
    }

    /// <summary>
    /// Case-insensitive, normalization-aware duplicate check against an existing list.
    /// </summary>
    public static bool IsDuplicate(IEnumerable<string> existing, string path)
    {
        var target = Normalize(path);
        return existing.Any(candidate =>
            string.Equals(Normalize(candidate), target, StringComparison.OrdinalIgnoreCase));
    }
}