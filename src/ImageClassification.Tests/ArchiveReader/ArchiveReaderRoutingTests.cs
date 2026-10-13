using ImageClassification.ArchiveReader;
using ImageClassification.Core.Interfaces;

namespace ImageClassification.Tests.ArchiveReader;

/// <summary>
/// Routing tests: a .cbz is claimed by the ZIP handler only when the file CONTENT is a
/// ZIP. Mislabeled files (classic case: a RAR-based comic saved as .cbz) fall through
/// to the 7-Zip handler, which reads classic RAR natively and otherwise reports a
/// human-readable note instead of .NET's cryptic "End of Central Directory record
/// could not be found."
/// </summary>
public sealed class ArchiveReaderRoutingTests
{
    // "Rar!\x1a\x07\x00" = classic RAR, "Rar!\x1a\x07\x01" = RAR5
    private static byte[] RarMagic(byte fifthByte)
    {
        var header = new byte[64 * 1024];
        header[0] = (byte)'R';
        header[1] = (byte)'a';
        header[2] = (byte)'r';
        header[3] = (byte)'!';
        header[4] = 0x1a;
        header[5] = 0x07;
        header[6] = fifthByte;
        return header;
    }

    [Fact]
    public async Task CbzContainingZipContent_StillUsesZipHandler_AndListsEntries()
    {
        string path = CreateTempPath("zip-cbz");
        string archivePath = path + ".cbz";
        CreateZipArchive(archivePath, new[] { ("page-001.jpg", new byte[] { 0x01 }) });

        try
        {
            using var reader = new global::ImageClassification.ArchiveReader.ArchiveReader();
            using IArchiveSession session = await reader.OpenSessionAsync(archivePath);

            Assert.Null(session.LastNote);
            Assert.Contains("page-001.jpg", session.ImageEntries);
        }
        finally
        {
            Cleanup(archivePath);
        }
    }

    [Fact]
    public async Task CbzContainingRar4Content_RoutesTo7Zip_NoCrypticZipError()
    {
        // A RAR4 signature inside a .cbz must NOT be handed to the ZIP handler (whose
        // .NET error "End of Central Directory record could not be found" was
        // incomprehensible to users). The 7-Zip handler claims it; a signature-only
        // file cannot be opened, so the note is the friendly failure text.
        string archivePath = CreateTempPath("rar4-cbz") + ".cbz";
        File.WriteAllBytes(archivePath, RarMagic(0x00));

        try
        {
            using var reader = new global::ImageClassification.ArchiveReader.ArchiveReader();
            using IArchiveSession session = await reader.OpenSessionAsync(archivePath);

            Assert.NotNull(session.LastNote);
            Assert.DoesNotContain("Central Directory", session.LastNote);
            Assert.Contains("cannot be opened as an archive", session.LastNote);
            Assert.Empty(session.ImageEntries);
        }
        finally
        {
            Cleanup(archivePath);
        }
    }

    [Fact]
    public async Task CbzContainingRar5Content_RoutesTo7Zip_NoCrypticZipError()
    {
        string archivePath = CreateTempPath("rar5-cbz") + ".cbz";
        File.WriteAllBytes(archivePath, RarMagic(0x01));

        try
        {
            using var reader = new global::ImageClassification.ArchiveReader.ArchiveReader();
            using IArchiveSession session = await reader.OpenSessionAsync(archivePath);

            Assert.NotNull(session.LastNote);
            Assert.DoesNotContain("Central Directory", session.LastNote);
            Assert.Contains("cannot be opened as an archive", session.LastNote);
            Assert.Empty(session.ImageEntries);
        }
        finally
        {
            Cleanup(archivePath);
        }
    }

    [Fact]
    public async Task CbzContainingNonArchiveContent_ReportsFriendlyZipFailure()
    {
        // Unknown signature: stays with the ZIP handler (safety net), and .NET's
        // "End of Central Directory record could not be found" is translated.
        string archivePath = CreateTempPath("text-cbz") + ".cbz";
        await File.WriteAllTextAsync(archivePath, "this is not an archive at all");

        try
        {
            using var reader = new global::ImageClassification.ArchiveReader.ArchiveReader();
            using IArchiveSession session = await reader.OpenSessionAsync(archivePath);

            Assert.NotNull(session.LastNote);
            Assert.DoesNotContain("Central Directory", session.LastNote);
            Assert.Contains("not a valid ZIP archive", session.LastNote);
            Assert.Contains(archivePath, session.LastNote);
            Assert.Empty(session.ImageEntries);
        }
        finally
        {
            Cleanup(archivePath);
        }
    }

    [Fact]
    public void ContainerFailureNotes_Build_TranslatesCrypticMessages()
    {
        string archivePath = @"C:\Comics\broken.cbz";

        string eocd = global::ImageClassification.ArchiveReader.ArchiveReader.ContainerFailureNotes.Build(
            archivePath,
            new InvalidDataException($"Could not find file: 'broken.cbz'. End of Central Directory record could not be found."));
        Assert.Contains("not a valid ZIP archive", eocd);
        Assert.Contains(archivePath, eocd); // file name substituted with the full path
        Assert.DoesNotContain("Central Directory", eocd);

        string unable = global::ImageClassification.ArchiveReader.ArchiveReader.ContainerFailureNotes.Build(
            archivePath,
            new SevenZipExtractor.SevenZipException("Unable to open archive"));
        Assert.Contains("cannot be opened as an archive", unable);
        Assert.Contains("skipped as empty", unable);

        // Unrecognized messages pass through untouched (path prefix + library text).
        string other = global::ImageClassification.ArchiveReader.ArchiveReader.ContainerFailureNotes.Build(
            archivePath, new InvalidOperationException("some other failure"));
        Assert.Contains("some other failure", other);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string CreateTempPath(string tag)
        => Path.Combine(Path.GetTempPath(), $"archive-route-{tag}-{Guid.NewGuid():N}");

    private static void CreateZipArchive(string archivePath, (string EntryName, byte[] Content)[] entries)
    {
        using var fs = new FileStream(archivePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: false);
        foreach (var (entryName, content) in entries)
        {
            var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(content, 0, content.Length);
        }
    }

    private static void Cleanup(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}