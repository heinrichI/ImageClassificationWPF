namespace ImageClassification.Core.Interfaces;

/// <summary>
/// Interface for extracting cover images from comic archives (CBZ/CBR).
/// </summary>
public interface IArchiveReader : IDisposable
{
    /// <summary>
    /// Extracts the first image file from a CBZ/CBR archive into memory.
    /// </summary>
    /// <param name="archivePath">Full path to the .cbz or .cbr archive.</param>
    /// <returns>
    /// Image bytes, or <c>null</c> if no image entry was found or the archive could not be opened.
    /// </returns>
    Task<byte[]?> ExtractFirstImageAsync(string archivePath);

    /// <summary>
    /// Extracts the image at the specified index (natural sort order) from the archive into memory.
    /// </summary>
    /// <param name="archivePath">Full path to the archive.</param>
    /// <param name="index">Zero-based index of the image entry to extract.</param>
    /// <returns>
    /// Image bytes, or <c>null</c> if no image entry was found at that index.
    /// </returns>
    Task<byte[]?> ExtractImageByIndexAsync(string archivePath, int index);

    /// <summary>
    /// Extracts the image at the specified index (natural sort order) from the archive into memory.
    /// </summary>
    /// <param name="archivePath">Full path to the archive.</param>
    /// <param name="index">Zero-based index of the image entry to extract.</param>
    /// <returns>
    /// Image bytes, or <c>null</c> if no image entry was found at that index.
    /// </returns>
    Task<byte[]?> ExtractImageToMemoryAsync(string archivePath, int index);

    /// <summary>
    /// Returns the total number of image entries in the archive.
    /// </summary>
    Task<int> GetImageCountAsync(string archivePath);

    /// <summary>
    /// Opens the archive and returns a session that enumerates all image entries once
    /// and can extract any page from the already-open handle (no re-open per page).
    /// Never throws for unreadable or empty archives — an empty session
    /// (<see cref="IArchiveSession.ImageEntries"/> is empty) is returned instead.
    /// </summary>
    /// <param name="archivePath">Full path to the .cbz/.cbr/.cb7/.cbt archive.</param>
    Task<IArchiveSession> OpenSessionAsync(string archivePath);
}