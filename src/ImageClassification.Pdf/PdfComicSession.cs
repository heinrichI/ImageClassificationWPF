using System.IO;
using ImageClassification.Core.Interfaces;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ImageClassification.Pdf;

/// <summary>
/// An <see cref="IArchiveSession"/> over a PDF document. One entry = one page that contains at
/// least one real (non-mask) image; extraction returns that page's FIRST real image
/// (the choice DupTerminator makes). The stream is opened with permissive sharing so the search
/// and the UI thumbnail pipeline can open the same PDF concurrently.
/// </summary>
internal sealed class PdfComicSession : IArchiveSession
{
    private readonly FileStream _stream;
    private readonly PdfDocument _document;
    private readonly List<int> _pageNumbers;

    public PdfComicSession(string path)
    {
        _stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write | FileShare.Delete);
        _document = PdfDocument.Open(
            _stream,
            new ParsingOptions
            {
                UseLenientParsing = true,
                SkipMissingFonts = true,
                FilterProvider = ComicPdfFilterProvider.Instance,
            });

        // One pass over the pages: remember which pages carry a real (non-mask) image.
        var pages = new List<int>();
        for (var pageNumber = 1; pageNumber <= _document.NumberOfPages; pageNumber++)
        {
            var image = FirstRealImage(_document.GetPage(pageNumber));
            if (image is not null)
            {
                pages.Add(pageNumber);
            }
        }

        _pageNumbers = pages;
        ImageEntries = pages.Select(n => $"page={n}").ToList().AsReadOnly();
    }

    public IReadOnlyList<string> ImageEntries { get; }

    public string? LastNote { get; }

    /// <summary>The first non-mask image on the page, or null if the page has none.</summary>
    private static IPdfImage? FirstRealImage(Page page)
    {
        return page.GetImages().FirstOrDefault(image => !image.IsImageMask);
    }

    /// <inheritdoc />
    public Task<byte[]?> ExtractToMemoryAsync(int index)
    {
        if (index < 0 || index >= _pageNumbers.Count)
        {
            return Task.FromResult<byte[]?>(null);
        }

        var image = FirstRealImage(_document.GetPage(_pageNumbers[index]));
        if (image is null)
        {
            return Task.FromResult<byte[]?>(null);
        }

        var raw = image.RawBytes;
        if (raw.IsEmpty)
        {
            return Task.FromResult<byte[]?>(null);
        }

        // Pass container formats through untouched (scans are usually DCT/JPEG) — no re-encode.
        if (HasImageFileMagic(raw))
        {
            return Task.FromResult<byte[]?>(raw.ToArray());
        }

        // Raw pixel buffer (e.g. FlateDecode): ImageSharp needs a container, so re-encode as PNG.
        return Task.FromResult<byte[]?>(image.TryGetPng(out var png) ? png : null);
    }

    /// <inheritdoc />
    public async Task<byte[]?[]> ExtractAllToMemoryAsync()
    {
        var result = new List<byte[]?>(_pageNumbers.Count);
        foreach (var index in Enumerable.Range(0, _pageNumbers.Count))
        {
            result.Add(await ExtractToMemoryAsync(index).ConfigureAwait(false));
        }
        return result.ToArray();
    }

    /// <summary>
    /// Magic-byte sniff: JPEG / PNG / BMP / TIFF are understood by the downstream ImageSharp
    /// decoder; anything else (raw pixel buffers) must be re-encoded first.
    /// </summary>
    private static bool HasImageFileMagic(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4)
        {
            return false;
        }

        if (b[0] == 0xFF && b[1] == 0xD8)
        {
            return true; // JPEG
        }

        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
        {
            return true; // PNG
        }

        if (b[0] == (byte)'B' && b[1] == (byte)'M')
        {
            return true; // BMP
        }

        if (b[0] == (byte)'I' && b[1] == (byte)'I' && (b[2] == 0x21 || b[2] == 0x10))
        {
            return true; // TIFF little-endian
        }

        if (b[0] == (byte)'M' && b[1] == (byte)'M' && (b[2] == 0x01 || b[2] == 0x28))
        {
            return true; // TIFF big-endian
        }

        return false;
    }

    public void Dispose()
    {
        _document.Dispose();
        _stream.Dispose();
    }
}