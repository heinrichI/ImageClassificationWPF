using System.Collections.Concurrent;
using ImageClassification.Core.Interfaces;
using SevenZipExtractor;

namespace ImageClassification.ArchiveReader;

/// <summary>
/// Reads CBZ/CBR archives using SevenZipExtractor and extracts the first image found.
/// Thread-safe for concurrent extraction across a shared instance.
/// </summary>
internal sealed class ArchiveReader : IArchiveReader
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tiff", ".tif"
    };

    private readonly ConcurrentBag<string> _tempFiles = new();

    /// <inheritdoc />
    public Task<string?> ExtractFirstImageAsync(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            throw new ArgumentException("Archive path must not be null or empty.", nameof(archivePath));

        if (!File.Exists(archivePath))
            return Task.FromResult<string?>(null);

        try
        {
            using var archiveFile = new ArchiveFile(archivePath);

            // Find the first entry that is not a folder and has an image extension
            var imageEntry = archiveFile.Entries
                .FirstOrDefault(e =>
                    !e.IsFolder &&
                    e.Size > 0 &&
                    ImageExtensions.Contains(Path.GetExtension(e.FileName)));

            if (imageEntry is null)
                return Task.FromResult<string?>(null);

            var ext = Path.GetExtension(imageEntry.FileName);
            if (string.IsNullOrEmpty(ext))
                ext = ".jpg";

            var tempPath = Path.Combine(Path.GetTempPath(), $"comic_{Guid.NewGuid():N}{ext}");

            imageEntry.Extract(tempPath);

            _tempFiles.Add(tempPath);

            return Task.FromResult<string?>(tempPath);
        }
        catch (Exception)
        {
            // Archive is corrupt, not a valid archive, or cannot be opened
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Removes all temporary files created by this instance.
    /// </summary>
    public void Cleanup()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
        _tempFiles.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }
}