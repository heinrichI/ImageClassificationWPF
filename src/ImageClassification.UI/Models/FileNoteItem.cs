namespace ImageClassification.UI.Models;

/// <summary>
/// One entry of the Search Image "Skipped files" list: the note text shown to the
/// user plus the path it refers to (a file that could not be decoded, or a missing
/// directory) so the per-entry menu can copy the path or open it in Explorer.
/// Immutable — notes are appended, never edited.
/// </summary>
public sealed class FileNoteItem
{
    public FileNoteItem(string text, string? path)
    {
        Text = text;
        Path = path;
    }

    /// <summary>Note text shown to the user (e.g. "Skipped C:\a.jpg: could not be read or decoded").</summary>
    public string Text { get; }

    /// <summary>Path the note refers to (file or directory); null for generic notes.</summary>
    public string? Path { get; }
}