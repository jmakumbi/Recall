using System.Runtime.InteropServices;
using System.Text;

namespace Recall.Discovery;

/// <summary>
/// Thin P/Invoke wrapper around Everything64.dll.
/// The DLL is loaded at runtime via NativeLibrary + DllImportResolver so the
/// path can be configured without the DLL being on PATH.
/// </summary>
public sealed class EverythingClient
{
    // ── DLL name used in [DllImport] ─────────────────────────────────────
    private const string DllName = "Everything64";

    // ── Request flag constants ────────────────────────────────────────────
    private const uint EVERYTHING_REQUEST_FILE_NAME    = 0x00000001;
    private const uint EVERYTHING_REQUEST_PATH         = 0x00000002;
    private const uint EVERYTHING_REQUEST_DATE_MODIFIED = 0x00000010;
    private const uint EVERYTHING_REQUEST_SIZE         = 0x00000020;

    private const uint EVERYTHING_ERROR_IPC = 2; // Everything IPC not available

    // ── Static resolver — registered once per assembly ───────────────────
    private static string _resolvedDllPath = @"libs\Everything64.dll";
    private static int    _resolverRegistered = 0;

    private static void EnsureResolver(string dllPath)
    {
        _resolvedDllPath = Path.GetFullPath(dllPath);
        if (Interlocked.CompareExchange(ref _resolverRegistered, 1, 0) == 0)
        {
            NativeLibrary.SetDllImportResolver(typeof(EverythingClient).Assembly,
                static (name, _, _) =>
                {
                    if (name != DllName) return IntPtr.Zero;
                    return NativeLibrary.TryLoad(_resolvedDllPath, out var handle)
                        ? handle : IntPtr.Zero;
                });
        }
    }

    // ── P/Invoke declarations ─────────────────────────────────────────────
    [DllImport(DllName, EntryPoint = "Everything_SetSearchW",      CharSet = CharSet.Unicode)] private static extern void     SetSearch(string search);
    [DllImport(DllName, EntryPoint = "Everything_SetRequestFlags"                           )] private static extern void     SetRequestFlags(uint flags);
    [DllImport(DllName, EntryPoint = "Everything_SetMax"                                    )] private static extern void     SetMax(uint max);
    [DllImport(DllName, EntryPoint = "Everything_QueryW"                                    )] private static extern bool     Query(bool wait);
    [DllImport(DllName, EntryPoint = "Everything_GetNumResults"                             )] private static extern uint     GetNumResults();
    [DllImport(DllName, EntryPoint = "Everything_GetResultFullPathNameW", CharSet = CharSet.Unicode)] private static extern void GetResultFullPathName(uint index, StringBuilder buf, uint bufSize);
    [DllImport(DllName, EntryPoint = "Everything_GetResultDateModified"                     )] private static extern bool     GetResultDateModified(uint index, out FILETIME ft);
    [DllImport(DllName, EntryPoint = "Everything_GetResultSize"                             )] private static extern bool     GetResultSize(uint index, out long size);
    [DllImport(DllName, EntryPoint = "Everything_CleanUp"                                   )] private static extern void     CleanUp();
    [DllImport(DllName, EntryPoint = "Everything_GetLastError"                              )] private static extern uint     GetLastError();

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low; public uint High; }

    // ── Instance ──────────────────────────────────────────────────────────
    private readonly DiscoveryConfig _config;
    private bool _serviceWarningShown;

    public EverythingClient(DiscoveryConfig config)
    {
        _config = config;
        EnsureResolver(config.EverythingDllPath);
    }

    /// <summary>
    /// Search Everything for files matching <paramref name="query"/> across all
    /// configured <see cref="DiscoveryConfig.SearchPaths"/> (recursively).
    /// WdsSnippet, WdsKind, AlreadyIngested, IsStale are not populated here.
    /// </summary>
    public IReadOnlyList<DiscoveryResult> Search(string query)
    {
        var paths = _config.ResolvedPaths();

        // Build Everything query: scope to each configured path with OR (parents: prefix)
        // e.g.  parents:"C:\Users\james\Documents" | parents:"D:\Projects"  readme
        string scopePart = paths.Count > 0
            ? string.Join(" | ", paths.Select(p => $"parents:\"{p}\""))
            : $"parents:\"{Environment.ExpandEnvironmentVariables("%USERPROFILE%")}\"";

        var everythingQuery = $"({scopePart}) {query}";

        SetSearch(everythingQuery);
        SetRequestFlags(
            EVERYTHING_REQUEST_FILE_NAME |
            EVERYTHING_REQUEST_PATH      |
            EVERYTHING_REQUEST_DATE_MODIFIED |
            EVERYTHING_REQUEST_SIZE);
        SetMax(_config.MaxResults);

        if (!Query(wait: true))
        {
            var err = GetLastError();
            if (err == EVERYTHING_ERROR_IPC && !_serviceWarningShown)
            {
                _serviceWarningShown = true;
                Console.Error.WriteLine(
                    "[recall] Everything service not detected. " +
                    "First search may be slower while index builds.");
            }
            return [];
        }

        var count  = GetNumResults();
        var buf    = new StringBuilder(4096);
        var results = new List<DiscoveryResult>((int)count);

        for (uint i = 0; i < count; i++)
        {
            buf.Clear();
            GetResultFullPathName(i, buf, (uint)buf.Capacity);
            var fullPath = buf.ToString();

            GetResultSize(i, out var size);

            DateTime modified = DateTime.MinValue;
            if (GetResultDateModified(i, out var ft))
            {
                var ticks = ((long)ft.High << 32) | ft.Low;
                try { modified = DateTime.FromFileTimeUtc(ticks).ToLocalTime(); }
                catch { /* corrupt FILETIME — leave MinValue */ }
            }

            var fileName  = Path.GetFileName(fullPath);
            var extension = Path.GetExtension(fullPath).ToLowerInvariant();

            results.Add(new DiscoveryResult(
                fullPath, fileName, extension,
                size, modified,
                null, null, false, false));
        }

        CleanUp();
        return results;
    }
}
