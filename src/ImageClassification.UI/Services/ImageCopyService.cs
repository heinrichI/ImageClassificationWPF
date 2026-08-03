using System.IO;
using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using ImageClassification.UI.ViewModels;
using Microsoft.Extensions.Logging;

namespace ImageClassification.UI.Services;

/// <summary>
/// Result summary of an image copy operation.
/// </summary>
public sealed class CopySummary
{
    public int Copied { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Copies comic search result images into a destination folder.
/// Images are extracted from archives into memory and written directly to disk — no temp files.
/// </summary>
public sealed class ImageCopyService
{
    private readonly IArchiveReader _archiveReader;
    private readonly ILogger<ImageCopyService> _logger;

    public ImageCopyService(IArchiveReader archiveReader, ILogger<ImageCopyService> logger)
    {
        _archiveReader = archiveReader ?? throw new ArgumentNullException(nameof(archiveReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CopySummary> CopyImagesAsync(
        IReadOnlyList<ComicCoverItem> items,
        string destinationDirectory,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var summary = new CopySummary();
        Directory.CreateDirectory(destinationDirectory);

        var done = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var bytes = await _archiveReader.ExtractImageToMemoryAsync(item.ArchivePath, item.PageIndex)
                    .ConfigureAwait(false);
                if (bytes is null || bytes.Length == 0)
                {
                    summary.Skipped++;
                    summary.Errors.Add($"{item.ArchiveFileName}: image not found at page {item.PageIndex + 1}");
                }
                else
                {
                    var fileName = BuildFileName(item, bytes);
                    var destPath = ResolveDestinationPath(destinationDirectory, fileName);
                    await File.WriteAllBytesAsync(destPath, bytes, ct).ConfigureAwait(false);
                    summary.Copied++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to copy image from {ArchivePath} page {PageIndex}",
                    item.ArchivePath, item.PageIndex);
                summary.Skipped++;
                summary.Errors.Add($"{item.ArchiveFileName}: {ex.Message}");
            }

            progress?.Report(++done);
        }

        return summary;
    }

    internal static string BuildFileName(ComicCoverItem item, byte[] bytes)
    {
        var baseName = Path.GetFileNameWithoutExtension(item.ArchiveFileName);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "comic";
        return $"{baseName}_p{item.PageIndex + 1}{SniffExtension(bytes)}";
    }

    internal static string ResolveDestinationPath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
            return candidate;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; ; i++)
        {
            candidate = Path.Combine(directory, $"{baseName} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Detects the image format from magic bytes. Falls back to .jpg.
    /// </summary>
    internal static string SniffExtension(byte[] bytes)
    {
        if (bytes is null || bytes.Length < 4)
            return ".jpg";

        // JPEG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ".jpg";
        // PNG: 89 50 4E 47
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return ".png";
        // GIF: "GIF8"
        if (bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'8')
            return ".gif";
        // BMP: "BM"
        if (bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
            return ".bmp";
        // TIFF little/big endian
        if ((bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A && bytes[3] == 0x00) ||
            (bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00 && bytes[3] == 0x2A))
            return ".tif";
        // WebP: "RIFF"...."WEBP"
        if (bytes.Length >= 12 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return ".webp";

        return ".jpg";
    }
}
