using System.IO.Compression;
using ImageClassification.ArchiveReader;

namespace ImageClassification.Tests.ArchiveReader;

public sealed class ArchiveReaderSortTests
{
    [Fact]
    public async Task ExtractFirstImageAsync_PrefersPage000OverPage005_WhenPrefixNumbersMatch()
    {
        var archivePath = CreateTempArchivePath();
        var reader = new global::ImageClassification.ArchiveReader.ArchiveReader();

        try
        {
            CreateCbzArchive(archivePath, new[]
            {
                ("Archie (2015-) Vol. 02-005.jpg", new byte[] { 0x05 }),
                ("Archie (2015-) Vol. 02-000.jpg", new byte[] { 0x00 })
            });

            var coverBytes = await reader.ExtractFirstImageAsync(archivePath);

            Assert.NotNull(coverBytes);
            Assert.Equal(new byte[] { 0x00 }, coverBytes);
        }
        finally
        {
            CleanupFile(archivePath);
        }
    }

    [Fact]
    public async Task ExtractFirstImageAsync_PrefersBaseImageOverBaseUnderscore0001()
    {
        var archivePath = CreateTempArchivePath();
        var reader = new global::ImageClassification.ArchiveReader.ArchiveReader();

        try
        {
            CreateCbzArchive(archivePath, new[]
            {
                ("CCI08272016_0001.jpg", new byte[] { 0x01 }),
                ("CCI08272016.jpg", new byte[] { 0x00 })
            });

            var coverBytes = await reader.ExtractFirstImageAsync(archivePath);

            Assert.NotNull(coverBytes);
            Assert.Equal(new byte[] { 0x00 }, coverBytes);
        }
        finally
        {
            CleanupFile(archivePath);
        }
    }

    [Fact]
    public async Task ExtractFirstImageAsync_PrefersAlphabeticalHeritageCoverEntry()
    {
        var archivePath = CreateTempArchivePath();
        var reader = new global::ImageClassification.ArchiveReader.ArchiveReader();

        try
        {
            CreateCbzArchive(archivePath, new[]
            {
                ("Archie's Pal Jughead Annual 005-002.jpg", new byte[] { 0x02 }),
                ("Archie's Pal Jughead Annual 005-003.jpg", new byte[] { 0x03 }),
                ("Archie's Pal Jughead Annual 005-004.jpg", new byte[] { 0x04 }),
                ("Archie's Pal Jughead Annual 005-005.jpg", new byte[] { 0x05 }),
                ("Archie's Pal Jughead Annual 005-001_Heritage.jpg", new byte[] { 0x01 })
            });

            var coverBytes = await reader.ExtractFirstImageAsync(archivePath);

            Assert.NotNull(coverBytes);
            Assert.Equal(new byte[] { 0x01 }, coverBytes);
        }
        finally
        {
            CleanupFile(archivePath);
        }
    }

    [Fact]
    public async Task ExtractFirstImageAsync_PrefersDominantImgSeriesOverSingleDifferentPrefix()
    {
        var archivePath = CreateTempArchivePath();
        var reader = new global::ImageClassification.ArchiveReader.ArchiveReader();

        try
        {
            CreateCbzArchive(archivePath, new[]
            {
                ("zfurby2.jpg", new byte[] { 0x99 }),
                ("img230.jpg", new byte[] { 0x01 }),
                ("img231.jpg", new byte[] { 0x02 }),
                ("img232.jpg", new byte[] { 0x03 }),
                ("img233.jpg", new byte[] { 0x04 }),
                ("img234.jpg", new byte[] { 0x05 })
            });

            var coverBytes = await reader.ExtractFirstImageAsync(archivePath);

            Assert.NotNull(coverBytes);
            Assert.Equal(new byte[] { 0x01 }, coverBytes);
        }
        finally
        {
            CleanupFile(archivePath);
        }
    }

    [Fact]
    public async Task ExtractImageByIndexAsync_IsDeterministic_WhenNumericTokensAreEqual()
    {
        var archivePath = CreateTempArchivePath();
        var reader = new global::ImageClassification.ArchiveReader.ArchiveReader();

        try
        {
            CreateCbzArchive(archivePath, new[]
            {
                ("Issue Vol. 02-000-b.jpg", new byte[] { 0x0B }),
                ("Issue Vol. 02-000-a.jpg", new byte[] { 0x0A })
            });

            var firstBytes = await reader.ExtractImageByIndexAsync(archivePath, 0);
            var secondBytes = await reader.ExtractImageByIndexAsync(archivePath, 1);

            Assert.NotNull(firstBytes);
            Assert.NotNull(secondBytes);

            Assert.Equal(new byte[] { 0x0A }, firstBytes);
            Assert.Equal(new byte[] { 0x0B }, secondBytes);
        }
        finally
        {
            CleanupFile(archivePath);
        }
    }

    private static string CreateTempArchivePath()
    {
        return Path.Combine(Path.GetTempPath(), $"archive-sort-{Guid.NewGuid():N}.cbz");
    }

    private static void CreateCbzArchive(string archivePath, IEnumerable<(string EntryName, byte[] Content)> entries)
    {
        using var fs = new FileStream(archivePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

        foreach (var (entryName, content) in entries)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(content, 0, content.Length);
        }
    }

    private static void CleanupFile(string path)
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