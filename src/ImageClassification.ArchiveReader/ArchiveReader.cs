using System.IO.Compression;
using ImageClassification.Core.Interfaces;
using Microsoft.Extensions.Logging;
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

    private readonly ILogger<ArchiveReader>? _logger;

    public ArchiveReader(ILogger<ArchiveReader>? logger = null)
    {
        _logger = logger;
    }

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

            return Task.FromResult<IArchiveSession>(new SevenZipArchiveSession(archivePath, _logger));
        }
        catch (Exception ex)
        {
            // Corrupt/unsupported container — treat as an empty archive so callers
            // can skip it without special-casing failures. The diagnostic note rides
            // on the empty session so the UI can show why the archive was skipped.
            _logger?.LogDebug(ex, "Failed to open archive session for {Path} — treating as empty", archivePath);
            // Show the FULL path in the UI note: the exception message usually
            // carries only the file name, so substitute the full path for it.
            string fileName = Path.GetFileName(archivePath);
            string message = ex.Message.Contains(fileName, StringComparison.OrdinalIgnoreCase)
                ? ex.Message.Replace(fileName, archivePath, StringComparison.OrdinalIgnoreCase)
                : $"{archivePath}: {ex.Message}";
            string note = $"{message} — skipped as empty";
            return Task.FromResult<IArchiveSession>(new EmptyArchiveSession(note));
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
    /// <see cref="Instance"/> is the shared no-note variant (e.g. missing file);
    /// the per-archive variant carries the open-failure diagnostic in <see cref="LastNote"/>.
    /// </summary>
    private sealed class EmptyArchiveSession : IArchiveSession
    {
        public static readonly EmptyArchiveSession Instance = new();

        private readonly string? _note;

        public EmptyArchiveSession(string? note = null)
        {
            _note = note;
        }

        public IReadOnlyList<string> ImageEntries { get; } = Array.Empty<string>();

        public string? LastNote => _note;

        public Task<byte[]?> ExtractToMemoryAsync(int index) => Task.FromResult<byte[]?>(null);

        public Task<byte[]?[]> ExtractAllToMemoryAsync() => Task.FromResult<byte[]?[]>(new byte[0][]);

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

        public string? LastNote => null;

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

        /// <summary>
        /// Sequential in-memory extraction of every image entry.
        /// Zip has no solid compression — each entry inflates independently,
        /// so this costs the same as extracting the same set of pages one by one.
        /// </summary>
        public async Task<byte[]?[]> ExtractAllToMemoryAsync()
        {
            byte[]?[] result = new byte[_entries.Count][];
            for (int i = 0; i < _entries.Count; i++)
            {
                try
                {
                    await using var entryStream = _entries[i].Open();
                    using var memory = new MemoryStream();
                    await entryStream.CopyToAsync(memory).ConfigureAwait(false);
                    result[i] = memory.Length > 0 ? memory.ToArray() : null;
                }
                catch
                {
                    result[i] = null;
                }
            }
            return result;
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
        private readonly string _archivePath;
        private readonly ArchiveFile _archiveFile;
        private readonly List<Entry> _entries; // sorted image entries
        private readonly string[] _entryNames;
        private readonly ILogger? _logger;

        public string? LastNote { get; private set; }

        public SevenZipArchiveSession(string archivePath, ILogger? logger = null)
        {
            _archivePath = archivePath;
            _logger = logger;
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

            ReportContainerMismatchNote();
        }

        /// <summary>
        /// Extension/container sanity check at OPEN time (covers mode included — the bulk
        /// path is not guaranteed to run). The file signature, not the extension, decides
        /// how the archive is read, so a ZIP inside a .cbr opens fine — but surfacing the
        /// mismatch is how users find renamed/corrupt files. RAR and RAR5 count as one
        /// family (both are expected for .cbr/.cbt), so a RAR5 in a .cbr stays silent.
        /// </summary>
        private void ReportContainerMismatchNote()
        {
            // The pattern match null-checks and unwraps in one step.
            if (SevenZBulkExtractor.TryDetectHandler(_archivePath) is not Guid detected)
                return; // unknown signature — the open/bulk failure notes are the safety net

            Guid expected = GuessHandler(_archivePath);
            if (SevenZBulkExtractor.SameContainerFamily(detected, expected))
                return;

            string name = SevenZBulkExtractor.ContainerName(detected);
            LastNote = $"{_archivePath}: {name} container inside " +
                       $"'{Path.GetExtension(_archivePath)}' — opened with {name} handler";
            _logger?.LogInformation("[7z] {Note}", LastNote);
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

        /// <summary>
        /// One native single-pass extraction of all image entries, captured directly
        /// in memory (no temp files). For solid-compressed archives this decodes the
        /// solid block once (O(N)) instead of re-decoding its prefix per entry (O(N²)).
        /// <para>
        /// The container is detected from the file signature first (comic extensions are
        /// conventions, not formats — a ZIP inside a .cbr is common); the extension-based
        /// guess is only the fallback for unknown signatures. If the primary handler
        /// refuses to open the archive, the remaining 7z handlers are tried in turn,
        /// and only if all of them fail does the session fall back to per-entry
        /// extraction (e.g. encrypted or corrupted archive).
        /// </para>
        /// </summary>
        public Task<byte[]?[]> ExtractAllToMemoryAsync()
        {
            if (_entries.Count == 0)
                return Task.FromResult<byte[]?[]>(new byte[0][]);

            return Task.Run(() =>
            {
                try
                {
                    // 1. Primary: the detected container, or the extension-based guess
                    Guid primary = SevenZBulkExtractor.TryDetectHandler(_archivePath)
                                   ?? GuessHandler(_archivePath);

                    // 2. Attempts: primary first, then the remaining handlers (safety net)
                    Exception? last = null;
                    foreach (Guid handler in CandidateOrder(primary))
                    {
                        try
                        {
                            byte[]?[] result = SevenZBulkExtractor.ExtractAllImages(
                                _archivePath, _entryNames, handler);

                            if (handler != primary)
                            {
                                // The container differed from the primary choice — report it.
                                LastNote = $"{_archivePath}: " +
                                    $"{SevenZBulkExtractor.ContainerName(handler)} container inside " +
                                    $"'{Path.GetExtension(_archivePath)}' — re-extracted with " +
                                    $"{SevenZBulkExtractor.ContainerName(handler)} handler";
                                _logger?.LogInformation("[7z] {Note}", LastNote);
                            }
                            return result;
                        }
                        catch (SevenZBulkExtractor.SevenZipOpenFailedException ex)
                        {
                            last = ex;
                        }
                    }

                    throw last!; // every handler refused the archive
                }
                catch (Exception ex)
                {
                    // Fallback: per-entry extraction via the kept-open handle.
                    // Logged (not silent) so a bulk-pass failure cannot hide itself —
                    // it silently degrades a solid archive to slow O(N²) per-page decoding.
                    _logger?.LogWarning(ex,
                        "Bulk 7z extraction failed for {Path}: {Message} — falling back to per-entry extraction",
                        _archivePath, ex.Message);
                    LastNote = $"{_archivePath}: " +
                              $"bulk failed ({ex.Message}) — per-entry fallback";
                    byte[]?[] result = new byte[_entries.Count][];
                    for (int i = 0; i < _entries.Count; i++)
                    {
                        try
                        {
                            using var memory = new MemoryStream();
                            _entries[i].Extract(memory);
                            result[i] = memory.Length > 0 ? memory.ToArray() : null;
                        }
                        catch
                        {
                            result[i] = null;
                        }
                    }
                    return result;
                }
            });
        }

        /// <summary>
        /// Extension-based guess, used only when the file signature is unrecognized.
        /// .cbz files never reach this session (they use the managed ZipArchiveSession),
        /// the mapping is kept for exotic/unknown containers only.
        /// </summary>
        private static Guid GuessHandler(string path)
        {
            if (path.EndsWith(".cbr", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".cbt", StringComparison.OrdinalIgnoreCase))
                return SevenZBulkExtractor.RarHandlerId;
            if (path.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase))
                return SevenZBulkExtractor.ZipHandlerId;
            return SevenZBulkExtractor.SevenZipHandlerId;
        }

        private static IEnumerable<Guid> CandidateOrder(Guid primary)
        {
            yield return primary;
            foreach (Guid candidate in new[]
                     {
                         SevenZBulkExtractor.RarHandlerId,
                         SevenZBulkExtractor.Rar5HandlerId,
                         SevenZBulkExtractor.ZipHandlerId,
                         SevenZBulkExtractor.SevenZipHandlerId
                     })
                if (candidate != primary)
                    yield return candidate;
        }

        public void Dispose() => _archiveFile.Dispose();
    }
}