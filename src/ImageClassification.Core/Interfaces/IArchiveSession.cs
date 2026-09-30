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
}