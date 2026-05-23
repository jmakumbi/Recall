using System.Runtime.InteropServices;
using System.Text;

namespace Recall.Ingestion;

/// <summary>
/// Extracts plain text from arbitrary file types using the Windows IFilter COM interface.
/// Handles .docx, .pdf, .msg, .xlsx, .pptx, .txt, and any other type that has an
/// IFilter registered, without pulling in any managed file-format libraries.
/// </summary>
public class IFilterExtractor
{
    // ── HRESULT constants ─────────────────────────────────────────────────
    private const int S_OK                    = 0;
    private const int FILTER_E_NO_MORE_TEXT   = unchecked((int)0x80041700);
    private const int FILTER_E_NO_MORE_VALUES = unchecked((int)0x80041701);
    private const int FILTER_E_END_OF_CHUNKS  = unchecked((int)0x80041702);
    private const int FILTER_E_NO_TEXT        = unchecked((int)0x80041703);

    // ── COM types ─────────────────────────────────────────────────────────

    [Flags]
    private enum CHUNKSTATE : uint
    {
        CHUNK_TEXT  = 0x1,
        CHUNK_VALUE = 0x2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPSPEC
    {
        public uint ulKind;  // 1 = PRSPEC_PROPID
        public uint propid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FULLPROPSPEC
    {
        public Guid    guidPropSet;
        public PROPSPEC psProperty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STAT_CHUNK
    {
        public uint         idChunk;
        public uint         breakType;
        public CHUNKSTATE   flags;
        public uint         locale;
        public FULLPROPSPEC attribute;
        public uint         idChunkSource;
        public uint         cwcStartSource;
        public uint         cwcLenSource;
    }

    [ComImport]
    [Guid("89BCB740-6119-101A-BCB7-00DD010655AF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFilter
    {
        [PreserveSig] int Init(uint grfFlags, uint cAttributes, IntPtr aAttributes, out uint pdwFlags);
        [PreserveSig] int GetChunk(out STAT_CHUNK pStat);
        [PreserveSig] int GetText(ref uint pcwcBuffer, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] char[] awcBuffer);
        [PreserveSig] int GetValue(out IntPtr ppPropValue);
        [PreserveSig] int BindRegion(IntPtr origPos, ref Guid riid, out IntPtr ppunk);
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────

    [DllImport("query.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int LoadIFilter(
        string   pwcsPath,
        IntPtr   pUnkOuter,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppIUnk);

    // ── Public API ────────────────────────────────────────────────────────

    private readonly IngestionConfig _config;
    private readonly OpenXmlExtractor _fallback;

    public IFilterExtractor(IngestionConfig config)
    {
        _config   = config;
        _fallback = new OpenXmlExtractor(config);
    }

    /// <summary>
    /// Extract plain text from <paramref name="filePath"/> via IFilter.
    /// If IFilter times out or returns null, falls back to <see cref="OpenXmlExtractor"/>
    /// for Open XML formats (.docx, .xlsx, .pptx) and plain text files.
    /// Never throws to the caller.
    /// </summary>
    /// <remarks>
    /// IFilter COM objects require an STA thread. This method marshals onto a
    /// dedicated STA thread so callers don't need to worry about apartment state.
    /// </remarks>
    public virtual string? Extract(string filePath)
    {
        string? result = null;
        Exception? threadEx = null;

        var sta = new Thread(() =>
        {
            try   { result = ExtractOnSta(filePath); }
            catch (Exception ex) { threadEx = ex; }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.IsBackground = true;
        sta.Start();

        // IFilter COM calls are in-process for most file types (txt, docx, pdf).
        // 15 s is generous; if a server-activated filter hasn't responded by then,
        // fall back to the BCL Open XML extractor.
        bool finished = sta.Join(TimeSpan.FromSeconds(15));
        if (!finished)
        {
            Log($"IFilter timed out for '{filePath}' — trying Open XML fallback.");
            return _fallback.Extract(filePath);
        }

        if (threadEx is not null)
            Log($"IFilter extraction failed for '{filePath}': {threadEx.Message}");

        // If IFilter returned nothing, try the Open XML fallback
        if (result is null)
        {
            var fallbackResult = _fallback.Extract(filePath);
            if (fallbackResult is not null)
                Log($"IFilter returned null for '{filePath}' — used Open XML fallback.");
            return fallbackResult;
        }

        return result;
    }

    private string? ExtractOnSta(string filePath)
    {
        object? comObj = null;
        IFilter? filter = null;
        try
        {
            int hr = LoadIFilter(filePath, IntPtr.Zero, out comObj);
            if (hr != S_OK || comObj is null)
            {
                Log($"No IFilter for '{Path.GetExtension(filePath)}' (hr=0x{hr:X8}), skipping.");
                return null;
            }

            filter = comObj as IFilter;
            if (filter is null)
            {
                Log($"LoadIFilter returned object that doesn't implement IFilter for '{filePath}'.");
                return null;
            }

            filter.Init(0, 0, IntPtr.Zero, out _);

            var sb    = new StringBuilder(_config.MaxExtractedCharsPerFile);
            var buf   = new char[4096];
            int total = 0;

            while (total < _config.MaxExtractedCharsPerFile)
            {
                int chunkHr = filter.GetChunk(out var stat);

                if (chunkHr == FILTER_E_END_OF_CHUNKS) break;
                if (chunkHr != S_OK)                   continue;
                if ((stat.flags & CHUNKSTATE.CHUNK_TEXT) == 0) continue;

                while (total < _config.MaxExtractedCharsPerFile)
                {
                    uint count = (uint)buf.Length;
                    int textHr = filter.GetText(ref count, buf);

                    if (count > 0)
                    {
                        sb.Append(buf, 0, (int)count);
                        total += (int)count;
                    }

                    if (textHr == FILTER_E_NO_MORE_TEXT || textHr == FILTER_E_NO_TEXT) break;
                    if (textHr != S_OK) break;
                }
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }
        finally
        {
            if (filter is not null) Marshal.ReleaseComObject(filter);
            if (comObj is not null && !ReferenceEquals(comObj, filter))
                Marshal.ReleaseComObject(comObj);
        }
    }

    private static void Log(string msg) =>
        Console.Error.WriteLine($"[recall] {msg}");
}
