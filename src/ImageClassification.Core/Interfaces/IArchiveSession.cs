namespace ImageClassification.Core.Interfaces;

/// <summary>
/// A handle to an opened comic archive.
/// Image entries are enumerated exactly once when the handle is opened, so pages
/// can be extracted from the already-open handle without re-opening the archive
/// (and re-reading its index) for every page.
/// Instances are meant to be used by a single consumer at a time (sequential
/// <see cref="ExtractToMemoryAsync"/> calls); different archives get different handles.
/// </summary>
public interface IArchiveSession : IDisposable
{
    /// <summary>
    /// Sorted image entry names (deterministic natural order).
    /// The zero-based index into this list is the page index used by
    /// <see cref="ExtractToMemoryAsync"/> and by the embedding cache keys.
    /// </summary>
    IReadOnlyList<string> ImageEntries { get; }

    /// <summary>
    /// Extracts the image at the given sorted index into memory.
    /// </summary>
    /// <param name="index">Zero-based index of the image entry to extract.</param>
    /// <returns>
    /// Image bytes, or <c>null</c> if the index is out of range or extraction fails.
    /// </returns>
    Task<byte[]?> ExtractToMemoryAsync(int index);

    /// <summary>
    /// Extracts ALL image entries (in <see cref="ImageEntries"/> order) into memory
    /// in one pass.
    /// <para>
    /// For solid-compressed 7z-family archives this is a single sequential decode of
    /// the archive, which is far cheaper than per-entry extraction — the latter
    /// re-decodes the whole solid prefix for every entry (O(N²) in total).
    /// </para>
    /// </summary>
    /// <returns>
    /// Array aligned with <see cref="ImageEntries"/>; an element is the extracted image
    /// bytes, or <c>null</c> if that entry could not be extracted.
    /// </returns>
    Task<byte[]?[]> ExtractAllToMemoryAsync();

    /// <summary>
    /// Human-readable diagnostic collected while the session was used (open or extraction):
    /// e.g. an archive that cannot be opened at all ("... is not a known archive type —
    /// skipped as empty"), a container mismatch ("ZIP container inside .cbr — re-extracted
    /// with the ZIP handler"), or a bulk-pass fallback notice. <c>null</c> if nothing to report.
    /// Consumers may surface it in the UI; it is not an error condition by itself.
    /// </summary>
    string? LastNote { get; }
}