namespace ImageClassification.Core.Interfaces;

/// <summary>
/// Interface for extracting cover images from comic archives (CBZ/CBR).
/// </summary>
public interface IArchiveReader : IDisposable
{
    /// <summary>
    /// Extracts the first image file from a CBZ/CBR archive to a temporary path.
    /// </summary>
    /// <param name="archivePath">Full path to the .cbz or .cbr archive.</param>
    /// <returns>
    /// Full path to the extracted temp file, or <c>null</c> if no image entry was found
    /// or the archive could not be opened.
    /// </returns>
    Task<string?> ExtractFirstImageAsync(string archivePath);

    /// <summary>
    /// Extracts the image at the specified index (natural sort order) from the archive.
    /// </summary>
    /// <param name="archivePath">Full path to the archive.</param>
    /// <param name="index">Zero-based index of the image entry to extract.</param>
    /// <returns>
    /// Full path to the extracted temp file, or <c>null</c> if no image entry was found at that index.
    /// </returns>
    Task<string?> ExtractImageByIndexAsync(string archivePath, int index);

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
    /// Extracts all image entries from the archive, sorted by natural sort order.
    /// </summary>
    /// <param name="archivePath">Full path to the archive.</param>
    /// <returns>
    /// List of full paths to extracted temp files, sorted by natural sort order.
    /// </returns>
    Task<List<string>> ExtractAllImagesAsync(string archivePath);
}