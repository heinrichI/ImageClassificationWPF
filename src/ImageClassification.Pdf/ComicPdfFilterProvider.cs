using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Filters.Dct.JpegLibrary;
using UglyToad.PdfPig.Filters.Jbig2.PdfboxJbig2;
using UglyToad.PdfPig.Filters.Jpx.OpenJpeg;
using UglyToad.PdfPig.Tokens;

namespace ImageClassification.Pdf;

/// <summary>
/// Filter provider that adds support for the DCT (JPEG), JBIG2 and JPX (JPEG2000) filters on top
/// of PdfPig's standard filter set. Scanned comic pages are usually stored as DCT; JBIG2/JPX
/// cover professionally produced PDFs (the same set DupTerminator uses).
/// </summary>
internal sealed class ComicPdfFilterProvider : BaseFilterProvider
{
    public static readonly ComicPdfFilterProvider Instance = new();

    private ComicPdfFilterProvider() : base(GetDictionary())
    {
    }

    private static Dictionary<string, IFilter> GetDictionary()
    {
        // Extra filters
        var dct = new JpegLibraryDctDecodeFilter();
        var jbig2 = new PdfboxJbig2DecodeFilter();
        var jpx = new OpenJpegJpxDecodeFilter();

        // Standard PdfPig filters
        var ascii85 = new Ascii85Filter();
        var asciiHex = new AsciiHexDecodeFilter();
        var ccitt = new CcittFaxDecodeFilter();
        var flate = new FlateFilter();
        var runLength = new RunLengthFilter();
        var lzw = new LzwFilter();

        return new Dictionary<string, IFilter>
        {
            { NameToken.Ascii85Decode.Data, ascii85 },
            { NameToken.Ascii85DecodeAbbreviation.Data, ascii85 },
            { NameToken.AsciiHexDecode.Data, asciiHex },
            { NameToken.AsciiHexDecodeAbbreviation.Data, asciiHex },
            { NameToken.CcittfaxDecode.Data, ccitt },
            { NameToken.CcittfaxDecodeAbbreviation.Data, ccitt },
            { NameToken.DctDecode.Data, dct },
            { NameToken.DctDecodeAbbreviation.Data, dct },
            { NameToken.FlateDecode.Data, flate },
            { NameToken.FlateDecodeAbbreviation.Data, flate },
            { NameToken.Jbig2Decode.Data, jbig2 },
            { NameToken.JpxDecode.Data, jpx },
            { NameToken.RunLengthDecode.Data, runLength },
            { NameToken.RunLengthDecodeAbbreviation.Data, runLength },
            { NameToken.LzwDecode.Data, lzw },
            { NameToken.LzwDecodeAbbreviation.Data, lzw }
        };
    }
}