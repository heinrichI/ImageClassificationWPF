using System;
using System.IO;
using System.Threading.Tasks;
using ImageClassification.Core.Interfaces;
using ImageClassification.Pdf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace ImageClassification.Tests.Services;

/// <summary>
/// Tests the PDF container handler end to end: real PDFs are generated with the PdfPig writer
/// (JPEG pages + text-only pages) so the reader path (filters, entry discovery, extraction)
/// is exercised against real file structures, not fakes.
/// </summary>
public sealed class PdfContainerHandlerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ics_pdf_test_").FullName;

    private sealed class TempFile
    {
        public string Path { get; }

        public TempFile(string path)
        {
            Path = path;
        }
    }

    private TempFile CreateFile(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return new TempFile(path);
    }

    private static byte[] MakeJpegBytes()
    {
        using var image = new Image<Rgb24>(32, 32, new Rgb24(200, 30, 30));
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream);
        return stream.ToArray();
    }

    private static void CreateJpegPdf(string path, int pageCount)
    {
        var jpeg = MakeJpegBytes();
        var builder = new PdfDocumentBuilder();
        for (var i = 0; i < pageCount; i++)
        {
            var page = builder.AddPage(496, 701);
            page.AddJpeg(jpeg, new PdfRectangle(0, 0, 496, 701));
        }
        File.WriteAllBytes(path, builder.Build());
    }

    private static void CreateTextOnlyPdf(string path)
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(496, 701);
        File.WriteAllBytes(path, builder.Build());
    }

    [Fact]
    public void CanOpen_MatchesPdfExtensionCaseInsensitively()
    {
        var handler = new PdfContainerHandler();

        Assert.True(handler.CanOpen("C:\\comics\\book.pdf"));
        Assert.True(handler.CanOpen("C:\\comics\\BOOK.PDF"));
        Assert.False(handler.CanOpen("C:\\comics\\book.cbz"));
        Assert.False(handler.CanOpen("C:\\comics\\book.cb7"));
    }

    [Fact]
    public async Task OpensJpegPdf_FirstImagePerPage_PassesJpegBytesThrough()
    {
        var file = CreateFile("comic.pdf", Array.Empty<byte>());
        CreateJpegPdf(file.Path, 2);

        using var session = await new PdfContainerHandler().OpenSessionAsync(file.Path);

        Assert.Null(session.LastNote);
        Assert.Equal(2, session.ImageEntries.Count);
        Assert.Equal("page=1", session.ImageEntries[0]);
        Assert.Equal("page=2", session.ImageEntries[1]);

        var page1 = await session.ExtractToMemoryAsync(0);
        Assert.NotNull(page1);
        // DCT pass-through: the extracted bytes are the original JPEG (FFD8 magic).
        Assert.True(page1!.Length > 2);
        Assert.Equal(0xFF, page1[0]);
        Assert.Equal(0xD8, page1[1]);

        Assert.Null(await session.ExtractToMemoryAsync(99));
    }

    [Fact]
    public async Task TextOnlyPdf_OpensWithZeroEntries()
    {
        var file = CreateFile("text.pdf", Array.Empty<byte>());
        CreateTextOnlyPdf(file.Path);

        using var session = await new PdfContainerHandler().OpenSessionAsync(file.Path);

        Assert.Null(session.LastNote);
        Assert.Empty(session.ImageEntries);
        Assert.Null(await session.ExtractToMemoryAsync(0));
        Assert.Empty(await session.ExtractAllToMemoryAsync());
    }

    [Fact]
    public async Task CorruptFile_ReturnsEmptySessionWithNote()
    {
        var file = CreateFile("corrupt.pdf", System.Text.Encoding.ASCII.GetBytes("this is not a pdf"));

        using var session = await new PdfContainerHandler().OpenSessionAsync(file.Path);

        Assert.Empty(session.ImageEntries);
        Assert.False(string.IsNullOrEmpty(session.LastNote));
        Assert.Contains("corrupt.pdf", session.LastNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractAll_MatchesPerPageResults()
    {
        var file = CreateFile("all.pdf", Array.Empty<byte>());
        CreateJpegPdf(file.Path, 3);

        using var session = await new PdfContainerHandler().OpenSessionAsync(file.Path);

        var all = await session.ExtractAllToMemoryAsync();

        Assert.Equal(3, all.Length);
        for (var i = 0; i < all.Length; i++)
        {
            Assert.NotNull(all[i]);
            Assert.Equal(0xFF, all[i]![0]);
            Assert.Equal(0xD8, all[i]![1]);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}