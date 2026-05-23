namespace Recall.Discovery;

public record DiscoveryResult(
    string FullPath,
    string FileName,
    string Extension,
    long SizeBytes,
    DateTime LastModified,
    string? WdsSnippet,       // populated by WindowsSearchClient; null when not found in WDS index
    string? WdsKind,          // e.g. "document", "email", "spreadsheet"
    bool AlreadyIngested,     // populated by DiscoveryService via TrackerDb
    bool IsStale              // ingested but file has since been modified
)
{
    /// <summary>Normalised key used for dedup and TrackerDb lookups.</summary>
    public string NormalisedPath =>
        System.IO.Path.GetFullPath(FullPath).ToLowerInvariant().Replace('/', '\\');
}

public sealed class DiscoveryConfig
{
    public string   EverythingDllPath { get; init; } = @"libs\Everything64.dll";

    /// <summary>
    /// Directories to search, recursively. Supports %APPDATA% / %USERPROFILE% etc.
    /// Populated at runtime from appsettings.json → Discovery.SearchPaths.
    /// If empty on first run, the /setup command will prompt the user to configure it.
    /// </summary>
    public string[] SearchPaths { get; init; } = [];

    public uint MaxResults { get; init; } = 200;

    /// <summary>Returns expanded, deduplicated, existing paths.</summary>
    public IReadOnlyList<string> ResolvedPaths() =>
        SearchPaths
            .Select(p => Environment.ExpandEnvironmentVariables(p).TrimEnd('\\'))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
