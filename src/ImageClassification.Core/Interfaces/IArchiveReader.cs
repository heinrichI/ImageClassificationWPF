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
}