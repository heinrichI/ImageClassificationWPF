using System.Runtime.InteropServices;

namespace ImageClassification.ArchiveReader;

/// <summary>
/// Single-pass, in-memory bulk extraction for 7z-family archives (CBR/CB7/CBT).
/// <para>
/// Per-entry extraction from a solid-compressed 7z archive re-decodes the whole
/// solid prefix for every entry (O(N²)); one native <c>IInArchive.Extract</c>
/// call over the requested items decodes the block once (O(N)).
/// </para>
/// <para>
/// The SevenZipExtractor package exposes the low-level 7z SDK objects
/// (<c>IInArchive</c>, <c>IInStream</c>, callbacks) as <c>internal</c>, so this
/// class declares its own <c>[ComImport]</c> copies. COM interop is bound by
/// interface GUID, so these copies interoperate with the native objects created
/// by 7z.dll exactly like the package's own types.
/// </para>
/// <para>
/// No temp files are created — every image lands directly in a <see cref="MemoryStream"/>.
/// </para>
/// </summary>
internal static class SevenZBulkExtractor
{
    // 7z SDK handler class IDs (see SevenZipExtractor Formats.cs)
    public static readonly Guid SevenZipHandlerId = new("23170f69-40c1-278a-1000-000110070000");
    public static readonly Guid RarHandlerId = new("23170f69-40c1-278a-1000-000110030000");
    public static readonly Guid Rar5HandlerId = new("23170f69-40c1-278a-1000-000110CC0000");
    public static readonly Guid ZipHandlerId = new("23170f69-40c1-278a-1000-000110010000");

    /// <summary>
    /// Detects the archive container by its file signature (first 8 bytes).
    /// Comic archive extensions (.cbr/.cbz/.cb7) are conventions only — the real
    /// container differs more often than not (e.g. a ZIP inside a .cbr). Returns
    /// <c>null</c> when the signature is unknown or the file cannot be read.
    /// </summary>
    internal static Guid? TryDetectHandler(string path)
    {
        byte[] header;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            header = new byte[8];
            if (fs.Read(header, 0, header.Length) < 6)
                return null;
        }
        catch (IOException)
        {
            return null;
        }

        // "Rar!\x1a\x07\x00" = classic RAR, "Rar!\x1a\x07\x01" = RAR5
        if (header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21
            && header[4] == 0x1A && header[5] == 0x07)
        {
            return header[6] == 0x01 ? Rar5HandlerId : RarHandlerId;
        }

        // "PK\x03\x04" / "PK\x05\x06" / "PK\x07\x08" = ZIP
        if (header[0] == 0x50 && header[1] == 0x4B
            && (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07))
            return ZipHandlerId;

        // "7z\xFC\x07\x07" = 7z
        if (header[0] == 0x37 && header[1] == 0x7A
            && header[2] == 0xFC && header[3] == 0x07 && header[4] == 0x07)
            return SevenZipHandlerId;

        return null;
    }

    /// <summary>Short container name for diagnostics ("RAR", "ZIP", ...).</summary>
    internal static string ContainerName(Guid handlerId)
    {
        if (handlerId == RarHandlerId) return "RAR";
        if (handlerId == Rar5HandlerId) return "RAR5";
        if (handlerId == ZipHandlerId) return "ZIP";
        if (handlerId == SevenZipHandlerId) return "7z";
        return handlerId.ToString("N");
    }

    /// <summary>
    /// Container families for mismatch diagnostics: RAR and RAR5 are both legitimate
    /// payloads of .cbr/.cbt (classic RAR is the historical convention, RAR5 the
    /// modern one), so they compare as the same family; ZIP and 7z are distinct.
    /// Keeps the handler-classification knowledge in one place (this component owns
    /// the handler IDs); callers just ask whether two containers are "compatible".
    /// </summary>
    internal static bool SameContainerFamily(Guid a, Guid b)
    {
        if (a == b)
            return true;
        return IsRarFamily(a) && IsRarFamily(b);
    }

    private static bool IsRarFamily(Guid handlerId)
        => handlerId == RarHandlerId || handlerId == Rar5HandlerId;

    /// <summary>
    /// Opens the archive through the native 7z SDK and extracts the given image
    /// entries in ONE sequential pass into memory.
    /// </summary>
    /// <param name="archivePath">Path to the 7z/RAR archive.</param>
    /// <param name="imageEntryNames">Sorted image entry names (as reported by the 7z API,
    /// including relative sub-paths). The result array is aligned with this list.</param>
    /// <param name="handlerId">Handler class ID (SevenZip or Rar).</param>
    /// <returns>
    /// Array aligned with <paramref name="imageEntryNames"/>; an element is the
    /// extracted image bytes, or <c>null</c> if that entry could not be extracted.
    /// </returns>
    public static byte[]?[] ExtractAllImages(
        string archivePath,
        IReadOnlyList<string> imageEntryNames,
        Guid handlerId)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            throw new ArgumentException("Archive path is null or empty.", nameof(archivePath));
        if (imageEntryNames is null)
            throw new ArgumentNullException(nameof(imageEntryNames));
        if (imageEntryNames.Count == 0)
            return []; // nothing to extract — don't touch the native library

        IntPtr library = LoadLibrary(Resolve7zLibraryPath());
        if (library == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Unable to load 7z native library (error {Marshal.GetLastWin32Error()})");

        try
        {
            IntPtr createObjectPtr = GetProcAddress(library, "CreateObject");
            if (createObjectPtr == IntPtr.Zero)
                throw new InvalidOperationException("CreateObject not found in 7z native library");

            var createObject = (CreateObjectDelegate)Marshal.GetDelegateForFunctionPointer(
                createObjectPtr, typeof(CreateObjectDelegate));

            Guid classId = handlerId;
            Guid interfaceId = typeof(IInArchive).GUID;
            int hr = createObject(ref classId, ref interfaceId, out object? comObject);
            if (hr != 0 || comObject is null)
                throw new InvalidOperationException($"7z CreateObject failed: 0x{hr:X8}");

            var archive = (IInArchive)comObject;
            try
            {
                using var fileStream = new FileStream(
                    archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var inStream = new FileInStream(fileStream);

                ulong maxCheckStartPosition = 32 * 1024;
                hr = archive.Open(inStream, ref maxCheckStartPosition, null);
                if (hr != 0)
                    throw new SevenZipOpenFailedException(archivePath, handlerId, hr);

                uint itemCount = archive.GetNumberOfItems();

                // Map sorted-image-name -> sorted image index (first occurrence wins)
                var nameToImage = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < imageEntryNames.Count; i++)
                    nameToImage.TryAdd(imageEntryNames[i], i);

                // Map 7z item index -> sorted image index
                var itemToImage = new Dictionary<uint, int>();
                var prop = new PropVariant();
                for (uint i = 0; i < itemCount; i++)
                {
                    object? raw;
                    ushort vt = 0;
                    try
                    {
                        archive.GetProperty(i, ItemPropId.kpidPath, ref prop);
                        vt = prop.vt;
                        raw = prop.GetObject();
                        prop.Clear();
                    }
                    catch (Exception ex)
                    {
                        // Pinpoint diagnostics: report the exact archive item that breaks the
                        // name-matching pass (7z item index of itemCount, reported variant type).
                        throw new InvalidOperationException(
                            $"7z GetProperty(kpidPath) failed for item {i} of {itemCount} (vt={vt}): {ex.Message}", ex);
                    }

                    if (raw is string name && nameToImage.TryGetValue(name, out int imageIndex))
                        itemToImage.TryAdd(i, imageIndex);
                }

                var callback = new InMemoryExtractCallback(itemToImage, imageEntryNames.Count);

                // The 7z API requires a sorted index array; numItems == count, outIndex 0.
                var indices = itemToImage.Keys.OrderBy(k => k).ToArray();
                try
                {
                    hr = archive.Extract(indices, (uint)indices.Length, 0, callback);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"archive.Extract threw (itemCount={itemCount}, requestedIndices={indices.Length}, " +
                        $"handler={handlerId:D}): {ex.Message}", ex);
                }
                if (hr != 0)
                    throw new InvalidOperationException($"7z Extract failed: 0x{hr:X8}");

                callback.FinalizeResults();
                archive.Close();
                return callback.Results;
            }
            finally
            {
                Marshal.ReleaseComObject(archive);
            }
        }
        finally
        {
            FreeLibrary(library);
        }
    }

    /// <summary>
    /// Mirrors the library search of the SevenZipExtractor package:
    /// app base directory variants first, then the system 7-Zip installation.
    /// </summary>
    private static string Resolve7zLibraryPath()
    {
        string arch = IntPtr.Size == 4 ? "x86" : "x64";
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        string[] candidates =
        {
            Path.Combine(baseDir, $"7z-{arch}.dll"),
            Path.Combine(baseDir, "bin", $"7z-{arch}.dll"),
            Path.Combine(baseDir, "bin", arch, "7z.dll"),
            Path.Combine(baseDir, arch, "7z.dll"),
            Path.Combine(programFiles, "7-Zip", "7z.dll"),
        };

        foreach (string candidate in candidates)
            if (File.Exists(candidate))
                return candidate;

        throw new InvalidOperationException(
            "7z native library not found (looked in the app base directory and the 7-Zip installation)");
    }

    // ── kernel32 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The 7z SDK refused to open the archive with the requested handler
    /// (typically S_FALSE: the container is not what this handler parses).
    /// Carries the details so callers can retry with a different handler.
    /// </summary>
    internal sealed class SevenZipOpenFailedException(
        string archivePath, Guid handlerId, int hr) : Exception(
        $"7z Open failed for {Path.GetFileName(archivePath)}: 0x{hr:X8} ({ContainerName(handlerId)} handler)");


    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpLibFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateObjectDelegate(
        [In] ref Guid classID,
        [In] ref Guid interfaceID,
        [MarshalAs(UnmanagedType.Interface)] out object outObject);

    // ── 7z SDK interface copies (GUIDs are the interop contract) ─────────────

    [ComImport]
    [Guid("23170F69-40C1-278A-0000-000600600000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInArchive
    {
        [PreserveSig]
        int Open(
            IInStream stream,
            [In] ref ulong maxCheckStartPosition,
            [MarshalAs(UnmanagedType.Interface)] IArchiveOpenCallback? openArchiveCallback);

        void Close();

        uint GetNumberOfItems();

        void GetProperty(
            uint index,
            ItemPropId propID,
            ref PropVariant value);

        [PreserveSig]
        int Extract(
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] uint[] indices,
            uint numItems,
            int testMode,
            [MarshalAs(UnmanagedType.Interface)] IArchiveExtractCallback extractCallback);

        void GetArchiveProperty(uint propID, ref PropVariant value);

        uint GetNumberOfProperties();

        void GetPropertyInfo(
            uint index,
            [MarshalAs(UnmanagedType.BStr)] out string name,
            out ItemPropId propID,
            out ushort varType);

        uint GetNumberOfArchiveProperties();

        void GetArchivePropertyInfo(
            uint index,
            [MarshalAs(UnmanagedType.BStr)] string name,
            ref uint propID,
            ref ushort varType);
    }

    [ComImport]
    [Guid("23170F69-40C1-278A-0000-000600100000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IArchiveOpenCallback
    {
        void SetTotal(IntPtr files, IntPtr bytes);

        void SetCompleted(IntPtr files, IntPtr bytes);
    }

    [ComImport]
    [Guid("23170F69-40C1-278A-0000-000300010000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISequentialInStream
    {
        uint Read(
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] data,
            uint size);
    }

    [ComImport]
    [Guid("23170F69-40C1-278A-0000-000300030000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInStream
    {
        uint Read(
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] data,
            uint size);

        void Seek(long offset, uint seekOrigin, IntPtr newPosition);
    }

    [ComImport]
    [Guid("23170F69-40C1-278A-0000-000300020000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISequentialOutStream
    {
        [PreserveSig]
        int Write(IntPtr data, uint size, IntPtr processedSize);
    }

    [ComImport]
    [Guid("23170F69-40C1-278A-0000-000600200000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IArchiveExtractCallback
    {
        void SetTotal(ulong total);

        void SetCompleted([In] ref ulong completeValue);

        [PreserveSig]
        int GetStream(
            uint index,
            [MarshalAs(UnmanagedType.Interface)] out ISequentialOutStream? outStream,
            AskExtractMode askExtractMode);

        void PrepareOperation(AskExtractMode askExtractMode);

        void SetOperationResult(OperationResult operationResult);
    }

    private enum AskExtractMode : int
    {
        kExtract = 0,
        kTest,
        kSkip
    }

    private enum OperationResult : int
    {
        kOK = 0,
        kUnSupportedMethod,
        kDataError,
        kCRCError
    }

    private enum ItemPropId : uint
    {
        kpidPath = 3
    }

    // ── Stream/callback wrappers ─────────────────────────────────────────────

    /// <summary>
    /// Minimal copy of the package's <c>PropVariant</c> (ole32-backed),
    /// enough for reading <c>kpidPath</c> (VT_BSTR or a VT_VARIANT-wrapped value).
    /// The struct is deliberately padded to 48 bytes: 7z 25.x fills RAR properties
    /// through a 24-byte C VARIANT (VT_VARIANT keeps its inner variant inline in the
    /// union) and writes past the classic 16-byte layout, overflowing adjacent stack
    /// locals otherwise. <c>GetObjectForNativeVariant</c> and <c>PropVariantClear</c>
    /// only touch the first 16/24 bytes, so the padding is safe.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);

        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;

        // Overflow padding: keeps out-of-bounds native writes away from neighbouring locals.
        [FieldOffset(16)] private long _pad1;
        [FieldOffset(24)] private long _pad2;
        [FieldOffset(32)] private long _pad3;
        [FieldOffset(40)] private long _pad4;

        public void Clear()
        {
            if (vt == 0)
                return; // VT_EMPTY — nothing to clear
            PropVariantClear(ref this);
            vt = 0;
        }

        public object? GetObject()
        {
            if (vt == 0)
                return null;

            GCHandle handle = GCHandle.Alloc(this, GCHandleType.Pinned);
            try
            {
                return Marshal.GetObjectForNativeVariant(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }
    }

    private sealed class FileInStream : ISequentialInStream, IInStream, IDisposable
    {
        private readonly Stream _baseStream;

        public FileInStream(Stream baseStream)
        {
            _baseStream = baseStream;
        }

        public uint Read(byte[] data, uint size)
        {
            return (uint)_baseStream.Read(data, 0, (int)size);
        }

        public void Seek(long offset, uint seekOrigin, IntPtr newPosition)
        {
            long position = _baseStream.Seek(offset, (SeekOrigin)seekOrigin);
            if (newPosition != IntPtr.Zero)
                Marshal.WriteInt64(newPosition, position);
        }

        public void Dispose()
        {
            _baseStream.Dispose();
        }
    }

    private sealed class MemoryOutStream : ISequentialOutStream
    {
        private readonly Stream _baseStream;
        private byte[] _buffer = [];

        public MemoryOutStream(Stream baseStream)
        {
            _baseStream = baseStream;
        }

        public int Write(IntPtr data, uint size, IntPtr processedSize)
        {
            try
            {
                int n = (int)size;
                if (n > 0)
                {
                    if (data == IntPtr.Zero)
                    {
                        // 7z can pass a null data pointer (e.g. RAR zero-size flushes).
                        // The managed-array marshalling of this parameter would crash with an
                        // NullReferenceException inside the interop layer, so the raw-pointer
                        // form is used and the null case is skipped explicitly.
                        if (processedSize != IntPtr.Zero)
                            Marshal.WriteInt32(processedSize, 0);
                        return 0;
                    }

                    if (_buffer.Length < n)
                        _buffer = new byte[n];
                    Marshal.Copy(data, _buffer, 0, n);
                    _baseStream.Write(_buffer, 0, n);
                }

                if (processedSize != IntPtr.Zero)
                    Marshal.WriteInt32(processedSize, n);
                return 0;
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"MemoryOutStream.Write failed (size={size}, dataNull={data == IntPtr.Zero}, processedSizeNull={processedSize == IntPtr.Zero}): {ex.Message}", ex);
            }
        }
    }

    private sealed class InMemoryExtractCallback : IArchiveExtractCallback
    {
        private readonly Dictionary<uint, int> _itemToImage;
        private readonly MemoryStream[] _streams;

        public byte[]?[] Results { get; private set; } = [];

        public InMemoryExtractCallback(Dictionary<uint, int> itemToImage, int imageCount)
        {
            _itemToImage = itemToImage;
            _streams = new MemoryStream[imageCount];
        }

        public void SetTotal(ulong total)
        {
        }

        public void SetCompleted(ref ulong completeValue)
        {
        }

        public int GetStream(uint index, out ISequentialOutStream? outStream, AskExtractMode askExtractMode)
        {
            try
            {
                if (askExtractMode == AskExtractMode.kExtract
                    && _itemToImage.TryGetValue(index, out int imageIndex)
                    && _streams[imageIndex] is null)
                {
                    var stream = new MemoryStream();
                    _streams[imageIndex] = stream;
                    outStream = new MemoryOutStream(stream);
                    return 0; // S_OK — stream is provided
                }

                // 7z contract: S_OK must be accompanied by a valid outStream; items that are not
                // requested (including folder entries, which RAR archives store as real entries)
                // must be reported as S_FALSE, otherwise 7z dereferences a null stream.
                outStream = null;
                return 1; // S_FALSE — skip this item
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"GetStream failed (index={index}, ask={askExtractMode}, matches={_itemToImage.ContainsKey(index)}): {ex.Message}", ex);
            }
        }

        public void PrepareOperation(AskExtractMode askExtractMode)
        {
        }

        public void SetOperationResult(OperationResult operationResult)
        {
        }

        /// <summary>
        /// Converts the captured streams into the result array
        /// (null = entry missing or empty).
        /// </summary>
        public void FinalizeResults()
        {
            Results = new byte[_streams.Length][];
            for (int i = 0; i < _streams.Length; i++)
            {
                Results[i] = _streams[i] is { } stream && stream.Length > 0
                    ? stream.ToArray()
                    : null;
                _streams[i]?.Dispose();
            }
        }
    }
}