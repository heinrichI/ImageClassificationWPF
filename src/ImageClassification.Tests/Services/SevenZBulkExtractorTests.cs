using System.Reflection;
using System.Runtime.InteropServices;
using ImageClassification.ArchiveReader;

namespace ImageClassification.Tests.Services;

public class SevenZBulkExtractorTests
{
    /// <summary>
    /// 7z 25.x fills RAR archive properties through a 24-byte C VARIANT (VT_VARIANT
    /// keeps its inner variant inline in the union) and writes past the classic
    /// 16-byte PropVariant layout. The interop struct must carry enough padding to
    /// absorb that overflow, otherwise the native write corrupts neighbouring stack
    /// locals (observed as random NullReferenceExceptions in the name-matching loop).
    /// This guard keeps the padding from being lost in a future struct refactor.
    /// </summary>
    [Fact]
    public void PropVariant_IsPaddedToAbsorb_7z_VariantOverflow()
    {
        Type? propVariant = typeof(SevenZBulkExtractor).GetNestedType("PropVariant", BindingFlags.NonPublic);

        Assert.NotNull(propVariant);
        int size = Marshal.SizeOf(propVariant!);
        Assert.True(size >= 48,
            $"SevenZBulkExtractor.PropVariant is {size} bytes; it must be at least 48 " +
            "to absorb the 24-byte C VARIANT overflow of 7z 25.x RAR property writes.");
    }

    // ── Container signature detection (used when a bulk Open fails) ─────────

    private static string WriteHeaderFile(byte[] header)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ics_detect_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, header);
        return path;
    }

    [Fact]
    public void TryDetectHandler_ClassicRarSignature_ReturnsRarHandler()
    {
        string path = WriteHeaderFile(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0xCF });
        try
        {
            Assert.Equal(SevenZBulkExtractor.RarHandlerId, SevenZBulkExtractor.TryDetectHandler(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryDetectHandler_Rar5Signature_ReturnsRar5Handler()
    {
        string path = WriteHeaderFile(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 });
        try
        {
            Assert.Equal(SevenZBulkExtractor.Rar5HandlerId, SevenZBulkExtractor.TryDetectHandler(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryDetectHandler_ZipSignature_ReturnsZipHandler()
    {
        string path = WriteHeaderFile(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00 });
        try
        {
            Assert.Equal(SevenZBulkExtractor.ZipHandlerId, SevenZBulkExtractor.TryDetectHandler(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryDetectHandler_SevenZipSignature_ReturnsSevenZipHandler()
    {
        string path = WriteHeaderFile(new byte[] { 0x37, 0x7A, 0xFC, 0x07, 0x07, 0x00, 0x00, 0x00 });
        try
        {
            Assert.Equal(SevenZBulkExtractor.SevenZipHandlerId, SevenZBulkExtractor.TryDetectHandler(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryDetectHandler_UnknownSignature_ReturnsNull()
    {
        string path = WriteHeaderFile(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 });
        try
        {
            Assert.Null(SevenZBulkExtractor.TryDetectHandler(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryDetectHandler_TooShortFile_ReturnsNull()
    {
        string path = WriteHeaderFile(new byte[] { 0x50, 0x4B, 0x03 });
        try
        {
            Assert.Null(SevenZBulkExtractor.TryDetectHandler(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryDetectHandler_MissingFile_ReturnsNull()
    {
        Assert.Null(SevenZBulkExtractor.TryDetectHandler(
            Path.Combine(Path.GetTempPath(), $"ics_missing_{Guid.NewGuid():N}.bin")));
    }
}