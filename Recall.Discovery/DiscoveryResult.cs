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
    public string EverythingDllPath  { get; init; } = @"libs\Everything64.dll";
    public string DefaultSearchScope { get; init; } = "%USERPROFILE%";
    public uint   MaxResults         { get; init; } = 200;
}
