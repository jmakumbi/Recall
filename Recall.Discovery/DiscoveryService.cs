using Recall.Storage;

namespace Recall.Discovery;

/// <summary>
/// Orchestrates Everything + Windows Search queries, merges results,
/// and enriches with TrackerDb ingestion status.
/// </summary>
public sealed class DiscoveryService
{
    private EverythingClient    _everything;
    private WindowsSearchClient _wds;
    private readonly TrackerDb? _tracker;
    private DiscoveryConfig     _config;

    public DiscoveryService(DiscoveryConfig config, TrackerDb? tracker = null)
    {
        _config     = config;
        _everything = new EverythingClient(config);
        _wds        = new WindowsSearchClient(config);
        _tracker    = tracker;
    }

    /// <summary>
    /// Update the configured search paths at runtime (e.g. after /setup).
    /// Recreates the underlying clients with the new configuration.
    /// </summary>
    public void UpdateSearchPaths(string[] paths)
    {
        _config = new DiscoveryConfig
        {
            SearchPaths       = paths,
            EverythingDllPath = _config.EverythingDllPath,
            MaxResults        = _config.MaxResults
        };
        _everything = new EverythingClient(_config);
        _wds        = new WindowsSearchClient(_config);
    }

    /// <summary>
    /// Run both search backends across all <see cref="DiscoveryConfig.SearchPaths"/>,
    /// merge, deduplicate, and enrich with KB status.
    /// Everything results are canonical (MFT is authoritative for path/size/date).
    /// WDS results contribute AutoSummary and Kind where paths match.
    /// Returns empty with a console warning if no search paths are configured.
    /// </summary>
    public IReadOnlyList<DiscoveryResult> Search(string query)
    {
        if (_config.SearchPaths.Length == 0)
        {
            Console.Error.WriteLine(
                "[recall] No search paths configured. Run /setup to add folders to search.");
        }

        var everythingHits = _everything.Search(query);
        var wdsHits        = _wds.Search(query);

        return Merge(everythingHits, wdsHits);
    }

    // ── Merge logic ───────────────────────────────────────────────────────

    private IReadOnlyList<DiscoveryResult> Merge(
        IReadOnlyList<DiscoveryResult> everythingHits,
        IReadOnlyList<DiscoveryResult> wdsHits)
    {
        // Index WDS results by normalised path for O(1) enrichment lookup
        var wdsByPath = wdsHits
            .GroupBy(r => NormalisePath(r.FullPath))
            .ToDictionary(g => g.Key, g => g.First());

        // Start with Everything results (canonical); enrich from WDS where available
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged  = new List<DiscoveryResult>(everythingHits.Count + wdsHits.Count);

        foreach (var hit in everythingHits)
        {
            var key = NormalisePath(hit.FullPath);
            if (!seen.Add(key)) continue;

            wdsByPath.TryGetValue(key, out var wdsHit);
            var enriched = hit with
            {
                WdsSnippet = wdsHit?.WdsSnippet,
                WdsKind    = wdsHit?.WdsKind
            };
            merged.Add(Enrich(enriched));
        }

        // Append any WDS results that Everything didn't find
        foreach (var hit in wdsHits)
        {
            var key = NormalisePath(hit.FullPath);
            if (!seen.Add(key)) continue;
            merged.Add(Enrich(hit));
        }

        return merged;
    }

    private DiscoveryResult Enrich(DiscoveryResult result)
    {
        if (_tracker is null) return result;

        var tracked = _tracker.GetTrackedFile(result.FullPath);
        if (tracked is null)
            return result; // not in KB

        var isStale = tracked.LastModified < result.LastModified;
        return result with { AlreadyIngested = true, IsStale = isStale };
    }

    private static string NormalisePath(string path) =>
        Path.GetFullPath(path).ToLowerInvariant().Replace('/', '\\');
}
