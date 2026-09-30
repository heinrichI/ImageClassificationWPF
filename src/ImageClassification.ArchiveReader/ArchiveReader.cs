using System.IO.Compression;
using ImageClassification.Core.Interfaces;
using SevenZipExtractor;

namespace ImageClassification.ArchiveReader;

/// <summary>
/// Reads CBZ/CBR/CB7 archives and extracts images.
/// CBZ (ZIP) is handled via System.IO.Compression (fully managed, fast).
/// CBR/CB7 use SevenZipExtractor with native 7z.dll.
///
/// <see cref="OpenSessionAsync"/> is the primary entry point for bulk processing:
/// it opens the archive ONCE and returns a session from which any number of pages
/// can be extracted without re-opening the archive or re-reading its index.
/// The legacy per-page methods delegate to a session as well.
/// Thread-safe for concurrent use across a shared instance; a single
/// IArchiveSession should be consumed by one consumer at a time.
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
        using var session = await OpenSessionAsync(archivePath).ConfigureAwait(false);
        return await session.ExtractToMemoryAsync(index).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> GetImageCountAsync(string archivePath)
    {
        using var session = await OpenSessionAsync(archivePath).ConfigureAwait(false);
        return session.ImageEntries.Count;
    }

    /// <inheritdoc />
    public Task<IArchiveSession> OpenSessionAsync(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return Task.FromResult<IArchiveSession>(EmptyArchiveSession.Instance);

        try
        {
            if (archivePath.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<IArchiveSession>(new ZipArchiveSession(archivePath));

            return Task.FromResult<IArchiveSession>(new SevenZipArchiveSession(archivePath));
        }
        catch
        {
            // Corrupt/unsupported container — treat as an empty archive so callers
            // can skip it without special-casing failures.
            return Task.FromResult<IArchiveSession>(EmptyArchiveSession.Instance);
        }
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

    private static ImageEntrySortKey BuildSortKey(string fileName, int fallbackIndex)
    {
        return new ImageEntrySortKey(Path.GetFileName(fileName), fallbackIndex);
    }

    // ── Session implementations ─────────────────────────────────────────────

    /// <summary>
    /// Session for archives that cannot be opened or that contain no image entries.
    /// </summary>
    private sealed class EmptyArchiveSession : IArchiveSession
    {
        public static readonly EmptyArchiveSession Instance = new();

        public IReadOnlyList<string> ImageEntries { get; } = Array.Empty<string>();

        public Task<byte[]?> ExtractToMemoryAsync(int index) => Task.FromResult<byte[]?>(null);

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Session backed by a kept-open <see cref="ZipArchive"/> (CBZ).
    /// Image entries are enumerated once in the constructor; extraction reuses
    /// the open handle. Disposing the session closes the archive and its file stream.
    /// </summary>
    private sealed class ZipArchiveSession : IArchiveSession
    {
        private readonly ZipArchive _archive;
        private readonly List<ZipArchiveEntry> _entries; // sorted image entries
        private readonly string[] _entryNames;

        public ZipArchiveSession(string archivePath)
        {
            var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            var candidates = new List<(ZipArchiveEntry Entry, ImageEntrySortKey Key)>(_archive.Entries.Count);
            for (int i = 0; i < _archive.Entries.Count; i++)
            {
                var entry = _archive.Entries[i];
                if (entry.Length > 0 && ImageExtensions.Contains(Path.GetExtension(entry.Name)))
                {
                    candidates.Add((entry, BuildSortKey(entry.Name, i)));
                }
            }

            _entries = candidates
                .OrderBy(c => c.Key.NormalizedFileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Key.FallbackIndex)
                .Select(c => c.Entry)
                .ToList();

            _entryNames = _entries.Select(e => e.FullName).ToArray();
        }

        public IReadOnlyList<string> ImageEntries => _entryNames;

        public async Task<byte[]?> ExtractToMemoryAsync(int index)
        {
            if (index < 0 || index >= _entries.Count)
                return null;

            try
            {
                await using var entryStream = _entries[index].Open();
                using var memory = new MemoryStream();
                await entryStream.CopyToAsync(memory).ConfigureAwait(false);
                return memory.ToArray();
            }
            catch
            {
                return null;
            }
        }

        public void Dispose() => _archive.Dispose();
    }

    /// <summary>
    /// Session backed by a kept-open SevenZipExtractor <see cref="ArchiveFile"/>
    /// (CBR/CB7/CBT). Image entries are enumerated once in the constructor;
    /// extraction reuses the open handle.
    /// </summary>
    private sealed class SevenZipArchiveSession : IArchiveSession
    {
        private readonly ArchiveFile _archiveFile;
        private readonly List<Entry> _entries; // sorted image entries
        private readonly string[] _entryNames;

        public SevenZipArchiveSession(string archivePath)
        {
            _archiveFile = new ArchiveFile(archivePath);

            var candidates = new List<(Entry Entry, ImageEntrySortKey Key)>(_archiveFile.Entries.Count);
            for (int i = 0; i < _archiveFile.Entries.Count; i++)
            {
                var entry = _archiveFile.Entries[i];
                if (!entry.IsFolder && entry.Size > 0 &&
                    ImageExtensions.Contains(Path.GetExtension(entry.FileName)))
                {
                    candidates.Add((entry, BuildSortKey(entry.FileName, i)));
                }
            }

            _entries = candidates
                .OrderBy(c => c.Key.NormalizedFileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Key.FallbackIndex)
                .Select(c => c.Entry)
                .ToList();

            _entryNames = _entries.Select(e => e.FileName).ToArray();
        }

        public IReadOnlyList<string> ImageEntries => _entryNames;

        public Task<byte[]?> ExtractToMemoryAsync(int index)
        {
            if (index < 0 || index >= _entries.Count)
                return Task.FromResult<byte[]?>(null);

            try
            {
                using var memory = new MemoryStream();
                _entries[index].Extract(memory);
                return Task.FromResult<byte[]?>(memory.ToArray());
            }
            catch
            {
                return Task.FromResult<byte[]?>(null);
            }
        }

        public void Dispose() => _archiveFile.Dispose();
    }
}