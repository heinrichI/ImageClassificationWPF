using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImageClassification.ArchiveReader;
using ImageClassification.Core.Interfaces;
using ImageClassification.Core.Models;
using ImageClassification.UI.Services;
using ImageClassification.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ArchiveReaderImpl = ImageClassification.ArchiveReader.ArchiveReader;

namespace ImageClassification.Tests.Services;

public class ImageCopyServiceTests
{
    private static readonly byte[] PngBytes =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D
    };

    private static ImageCopyService CreateService(IArchiveReader reader) =>
        new(reader, NullLogger<ImageCopyService>.Instance);

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "imgcopy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task CopyImagesAsync_CopiesAllItemsAndReportsProgress()
    {
        var reader = new Mock<IArchiveReader>();
        reader.Setup(r => r.ExtractImageToMemoryAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(PngBytes);
        var service = CreateService(reader.Object);

        var items = new List<ComicCoverItem>
        {
            new() { ArchivePath = "a.cbz", ArchiveFileName = "a.cbz", PageIndex = 0 },
            new() { ArchivePath = "b.cbz", ArchiveFileName = "b.cbz", PageIndex = 3 }
        };
        var dest = TempDir();
        var progress = new List<int>();
        try
        {
            var summary = await service.CopyImagesAsync(items, dest, new Progress<int>(p => progress.Add(p)));

            Assert.Equal(2, summary.Copied);
            Assert.Equal(0, summary.Skipped);
            Assert.True(File.Exists(Path.Combine(dest, "a_p1.png")));
            Assert.True(File.Exists(Path.Combine(dest, "b_p4.png")));
            Assert.Equal(new[] { 1, 2 }, progress);
        }
        finally
        {
            Directory.Delete(dest, true);
        }
    }

    [Fact]
    public async Task CopyImagesAsync_Conflict_RenamesWithNumericSuffix()
    {
        var reader = new Mock<IArchiveReader>();
        reader.Setup(r => r.ExtractImageToMemoryAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(PngBytes);
        var service = CreateService(reader.Object);

        var dest = TempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dest, "a_p1.png"), PngBytes);
            File.WriteAllBytes(Path.Combine(dest, "a_p1 (1).png"), PngBytes);

            var items = new List<ComicCoverItem>
            {
                new() { ArchivePath = "a.cbz", ArchiveFileName = "a.cbz", PageIndex = 0 }
            };

            var summary = await service.CopyImagesAsync(items, dest);

            Assert.Equal(1, summary.Copied);
            Assert.True(File.Exists(Path.Combine(dest, "a_p1 (2).png")));
        }
        finally
        {
            Directory.Delete(dest, true);
        }
    }

    [Fact]
    public async Task CopyImagesAsync_ExtractReturnsNull_CountsSkipped()
    {
        var reader = new Mock<IArchiveReader>();
        reader.Setup(r => r.ExtractImageToMemoryAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((byte[]?)null);
        var service = CreateService(reader.Object);

        var dest = TempDir();
        try
        {
            var items = new List<ComicCoverItem>
            {
                new() { ArchivePath = "a.cbz", ArchiveFileName = "a.cbz", PageIndex = 0 }
            };

            var summary = await service.CopyImagesAsync(items, dest);

            Assert.Equal(0, summary.Copied);
            Assert.Equal(1, summary.Skipped);
            Assert.Single(summary.Errors);
        }
        finally
        {
            Directory.Delete(dest, true);
        }
    }

    [Fact]
    public async Task CopyImagesAsync_Cancelled_Throws()
    {
        var reader = new Mock<IArchiveReader>();
        reader.Setup(r => r.ExtractImageToMemoryAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(PngBytes);
        var service = CreateService(reader.Object);

        var dest = TempDir();
        try
        {
            var items = new List<ComicCoverItem>
            {
                new() { ArchivePath = "a.cbz", ArchiveFileName = "a.cbz", PageIndex = 0 }
            };

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.CopyImagesAsync(items, dest, ct: cts.Token));
        }
        finally
        {
            Directory.Delete(dest, true);
        }
    }

    [Fact]
    public async Task CopyImagesAsync_RealArchive_ExtractsToMemory()
    {
        var dir = TempDir();
        try
        {
            var archivePath = Path.Combine(dir, "comic.cbz");
            using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("page01.png");
                using var entryStream = entry.Open();
                entryStream.Write(PngBytes);
            }

            var service = CreateService(new ArchiveReaderImpl());
            var items = new List<ComicCoverItem>
            {
                new() { ArchivePath = archivePath, ArchiveFileName = "comic.cbz", PageIndex = 0 }
            };
            var dest = Path.Combine(dir, "out");

            var summary = await service.CopyImagesAsync(items, dest);

            Assert.Equal(1, summary.Copied);
            var written = Path.Combine(dest, "comic_p1.png");
            Assert.True(File.Exists(written));
            Assert.Equal(PngBytes, File.ReadAllBytes(written));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
