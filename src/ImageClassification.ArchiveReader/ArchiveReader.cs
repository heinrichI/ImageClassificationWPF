using System.IO.Compression;
using System.Text.RegularExpressions;
using ImageClassification.Core.Interfaces;
using SevenZipExtractor;

namespace ImageClassification.ArchiveReader;

/// <summary>
/// Reads CBZ/CBR/CB7 archives and extracts images.
/// CBZ (ZIP) is handled via System.IO.Compression (fully managed, fast).
/// CBR/CB7 use SevenZipExtractor with native 7z.dll.
/// Thread-safe for concurrent extraction across a shared instance.
/// </summary>
internal sealed class ArchiveReader : IArchiveReader
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tiff", ".tif"
    };

    private readonly record struct ImageEntrySortKey(
        string NormalizedFileName,
        int FallbackIndex);

    /// <inheritdoc />
    public Task<byte[]?> ExtractFirstImageAsync(string archivePath)
    {
        return ExtractImageToMemoryAsync(archivePath, 0);
    }

    /// <inheritdoc />
    public Task<byte[]?> ExtractImageByIndexAsync(string archivePath, int index)
    {
        return ExtractImageToMemoryAsync(archivePath, index);
    }

    /// <inheritdoc />
    public async Task<byte[]?> ExtractImageToMemoryAsync(string archivePath, int index)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            throw new ArgumentException("Archive path must not be null or empty.", nameof(archivePath));

        if (!File.Exists(archivePath))
            return null;

        if (index < 0)
            return null;

        try
        {
            var entries = GetSortedImageEntries(archivePath);
            if (index >= entries.Count)
                return null;

            var (entryName, _) = entries[index];
            return await ExtractEntryToMemoryAsync(archivePath, entryName).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task<int> GetImageCountAsync(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return Task.FromResult(0);

        try
        {
            var entries = GetSortedImageEntries(archivePath);
            return Task.FromResult(entries.Count);
        }
        catch
        {
            return Task.FromResult(0);
        }
    }

    /// <summary>
    /// Lists all image entries from the archive, sorted deterministically.
    /// For CBZ (ZIP), entries are read via System.IO.Compression.
    /// For CBR/CB7/CBT, entries are read via SevenZipExtractor.
    /// </summary>
    private List<(string EntryName, ImageEntrySortKey SortKey)> GetSortedImageEntries(string archivePath)
    {
        if (archivePath.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase))
            return GetSortedZipEntries(archivePath);
        return GetSortedSevenZipEntries(archivePath);
    }

    private List<(string EntryName, ImageEntrySortKey SortKey)> GetSortedZipEntries(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = new List<(string EntryName, ImageEntrySortKey SortKey)>(archive.Entries.Count);

        for (int i = 0; i < archive.Entries.Count; i++)
        {
            var entry = archive.Entries[i];
            if (entry.Length > 0 && ImageExtensions.Contains(Path.GetExtension(entry.Name)))
            {
                entries.Add((entry.FullName, BuildSortKey(entry.Name, i)));
            }
        }

        entries.Sort(CompareImageEntries);
        return entries;
    }

    private List<(string EntryName, ImageEntrySortKey SortKey)> GetSortedSevenZipEntries(string archivePath)
    {
        using var archiveFile = new ArchiveFile(archivePath);
        var entries = new List<(string EntryName, ImageEntrySortKey SortKey)>(archiveFile.Entries.Count);

        for (int i = 0; i < archiveFile.Entries.Count; i++)
        {
            var entry = archiveFile.Entries[i];
            if (!entry.IsFolder && entry.Size > 0 &&
                ImageExtensions.Contains(Path.GetExtension(entry.FileName)))
            {
                entries.Add((entry.FileName, BuildSortKey(entry.FileName, i)));
            }
        }

        entries.Sort(CompareImageEntries);
        return entries;
    }

    private static int CompareImageEntries(
        (string EntryName, ImageEntrySortKey SortKey) a,
        (string EntryName, ImageEntrySortKey SortKey) b)
    {
        var nameCompare = StringComparer.OrdinalIgnoreCase.Compare(
            a.SortKey.NormalizedFileName,
            b.SortKey.NormalizedFileName);
        if (nameCompare != 0)
            return nameCompare;

        return a.SortKey.FallbackIndex.CompareTo(b.SortKey.FallbackIndex);
    }

    private static ImageEntrySortKey BuildSortKey(string fileName, int fallbackIndex)
    {
        var normalizedFileName = Path.GetFileName(fileName);

        return new ImageEntrySortKey(
            NormalizedFileName: normalizedFileName,
            FallbackIndex: fallbackIndex);
    }

    /// <summary>
    /// Extracts a single entry from the archive to an in-memory byte array.
    /// </summary>
    private async Task<byte[]?> ExtractEntryToMemoryAsync(string archivePath, string entryName)
    {
        if (archivePath.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase))
            return await ExtractZipEntryToMemoryAsync(archivePath, entryName).ConfigureAwait(false);
        return ExtractSevenZipEntryToMemory(archivePath, entryName);
    }

    private async Task<byte[]?> ExtractZipEntryToMemoryAsync(string archivePath, string entryName)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry(entryName);
        if (entry is null || entry.Length == 0)
            return null;

        await using var entryStream = entry.Open();
        using var memory = new MemoryStream();
        await entryStream.CopyToAsync(memory).ConfigureAwait(false);
        return memory.ToArray();
    }

    private byte[]? ExtractSevenZipEntryToMemory(string archivePath, string entryName)
    {
        using var archiveFile = new ArchiveFile(archivePath);
        var imageEntry = archiveFile.Entries
            .FirstOrDefault(e => string.Equals(e.FileName, entryName, StringComparison.OrdinalIgnoreCase));

        if (imageEntry is null || imageEntry.IsFolder || imageEntry.Size == 0)
            return null;

        using var memory = new MemoryStream();
        imageEntry.Extract(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// No-op cleanup. Extraction is fully in-memory; no temp files are created.
    /// </summary>
    public void Cleanup()
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }
}