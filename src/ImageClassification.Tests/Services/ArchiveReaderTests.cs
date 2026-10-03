using System.IO.Compression;
using ImageClassification.Core.Interfaces;

namespace ImageClassification.Tests.Services;

/// <summary>
/// Tests for OpenSessionAsync session-level behaviour:
/// the contract that OpenSessionAsync never throws and that open-stage failures
/// carry a human-readable diagnostic in <see cref="IArchiveSession.LastNote"/>
/// so the UI can show why an archive was skipped.
/// </summary>
public class ArchiveReaderTests
{
    // Fully-qualified: the namespace ImageClassification.ArchiveReader would shadow the
    // type name if imported via a using directive.
    private readonly ImageClassification.ArchiveReader.ArchiveReader _reader = new();

    [Fact]
    public async Task OpenSessionAsync_UnknownContainer_ReturnsEmptySessionWithNote()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "junk.cbr");

        try
        {
            // Garbage payload: no PK / RAR / 7z signature — the 7z layer cannot
            // classify the container and refuses to open it.
            await File.WriteAllTextAsync(path, "this is definitely not an archive of any kind");

            IArchiveSession session = await _reader.OpenSessionAsync(path);

            Assert.Empty(session.ImageEntries);
            Assert.NotNull(session.LastNote);
            Assert.Contains("not a known archive type", session.LastNote);
            Assert.Contains("skipped as empty", session.LastNote);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task OpenSessionAsync_MissingFile_ReturnsEmptySessionWithoutNote()
    {
        // A missing file is a normal directory-scan outcome, not a diagnostic —
        // the shared no-note singleton must be returned.
        IArchiveSession session = await _reader.OpenSessionAsync(
            Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.cbr"));

        Assert.Empty(session.ImageEntries);
        Assert.Null(session.LastNote);
    }

    [Fact]
    public async Task OpenSessionAsync_ValidCbz_ReturnsSessionWithoutNote()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "valid.cbz");

        try
        {
            await using (var fs = File.Create(path))
            {
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true);
                using (var s0 = zip.CreateEntry("001.jpg").Open())
                    s0.WriteByte(0);
                await using (var s1 = zip.CreateEntry("002.jpg").Open())
                    await s1.WriteAsync(new byte[] { 1, 2, 3 });
                using (var s2 = zip.CreateEntry("readme.txt").Open())
                    s2.WriteByte(1);
                zip.Dispose();
            }

            IArchiveSession session = await _reader.OpenSessionAsync(path);

            // Only image entries are listed, in natural order.
            Assert.Equal(new[] { "001.jpg", "002.jpg" }, session.ImageEntries);
            Assert.Null(session.LastNote);
            session.Dispose();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task OpenSessionAsync_NullPath_ReturnsEmptySession()
    {
        IArchiveSession session = await _reader.OpenSessionAsync(string.Empty);

        Assert.Empty(session.ImageEntries);
        Assert.Null(session.LastNote);
    }

    [Fact]
    public async Task OpenSessionAsync_ZipContainerInsideCbr_LiveSessionWithMismatchNote()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "renamed.cbr");

        try
        {
            // A real ZIP renamed to .cbr — the common "wrong extension" case.
            // Signature detection picks the ZIP handler, the archive opens fine,
            // and the mismatch must be reported at OPEN time (covers mode included).
            await using (var fs = File.Create(path))
            {
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true);
                using (var s0 = zip.CreateEntry("001.jpg").Open())
                    s0.WriteByte(0);
                await using (var s1 = zip.CreateEntry("002.jpg").Open())
                    await s1.WriteAsync(new byte[] { 1, 2, 3 });
                zip.Dispose();
            }

            IArchiveSession session = await _reader.OpenSessionAsync(path);

            Assert.Equal(new[] { "001.jpg", "002.jpg" }, session.ImageEntries);
            Assert.NotNull(session.LastNote);
            Assert.Contains("ZIP container inside '.cbr'", session.LastNote);
            Assert.Contains(path, session.LastNote); // full path, for the UI notes list
            session.Dispose();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}