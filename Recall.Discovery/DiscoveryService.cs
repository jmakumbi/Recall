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

    // Supported extensions for filesystem fallback enumeration
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt",
        ".txt", ".md", ".csv", ".log", ".msg", ".eml", ".html", ".htm", ".rtf"
    };

    // Words stripped when building keyword search terms from natural language
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "find", "list", "show", "search", "get", "the", "a", "an", "in", "of",
        "all", "my", "files", "file", "documents", "document", "which", "where",
        "are", "is", "for", "with", "look", "locate", "give", "me", "some", "any",
        "pdf", "docx", "xlsx", "pptx", "doc", "xls", "ppt", "txt", "csv", "md",
        "recent", "new", "old", "modified", "created", "open", "read"
    };

    // Map of short names → canonical extensions for query parsing
    private static readonly Dictionary<string, string> ExtensionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"]  = ".pdf",  ["word"] = ".docx", ["docx"] = ".docx", ["doc"] = ".doc",
        ["excel"] = ".xlsx", ["xlsx"] = ".xlsx", ["xls"]  = ".xls",
        ["powerpoint"] = ".pptx", ["pptx"] = ".pptx", ["ppt"] = ".ppt",
        ["text"] = ".txt",  ["txt"]  = ".txt",  ["csv"]  = ".csv",
        ["markdown"] = ".md", ["md"] = ".md",   ["log"]  = ".log",
        ["email"] = ".msg", ["msg"]  = ".msg",  ["eml"]  = ".eml",
    };

    /// <summary>
    /// Run both search backends across all <see cref="DiscoveryConfig.SearchPaths"/>,
    /// merge, deduplicate, and enrich with KB status.
    /// Falls back to direct filesystem enumeration when neither backend finds results.
    /// Returns empty with a console warning if no search paths are configured.
    /// </summary>
    public IReadOnlyList<DiscoveryResult> Search(string query)
    {
        if (_config.SearchPaths.Length == 0)
        {
            Console.Error.WriteLine(
                "[recall] No search paths configured. Run /setup to add folders to search.");
            return [];
        }

        // Pre-process natural language query into structured search terms
        var (extensions, keywords, engineQuery) = ParseQuery(query);

        var everythingHits = _everything.Search(engineQuery);
        var wdsHits        = _wds.Search(engineQuery);

        // When both indexing engines return nothing, fall back to direct filesystem walk
        if (everythingHits.Count == 0 && wdsHits.Count == 0)
        {
            var fsHits = SearchFilesystem(extensions, keywords);
            return fsHits.Select(Enrich).ToList();
        }

        return Merge(everythingHits, wdsHits);
    }

    /// <summary>
    /// Parse a natural-language query into search components.
    /// Returns (target extensions, filename keywords, engine query string).
    /// </summary>
    private static (HashSet<string> Extensions, List<string> Keywords, string EngineQuery)
        ParseQuery(string query)
    {
        var lower   = query.ToLowerInvariant();
        var words   = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keywords   = new List<string>();

        foreach (var word in words)
        {
            if (ExtensionAliases.TryGetValue(word, out var ext))
                extensions.Add(ext);
            else if (!StopWords.Contains(word) && word.Length > 2)
                keywords.Add(word);
        }

        // Build a clean engine query: keywords only (no noise words, no extension names)
        // Everything and WDS search filenames/content, so we send just the meaningful terms.
        var engineQuery = keywords.Count > 0
            ? string.Join(' ', keywords)
            : query; // fall back to original if nothing survived stripping

        return (extensions, keywords, engineQuery);
    }

    /// <summary>
    /// Enumerate files directly from <see cref="DiscoveryConfig.SearchPaths"/>.
    /// Applies extension filter and optional filename keyword filter.
    /// </summary>
    private IReadOnlyList<DiscoveryResult> SearchFilesystem(
        HashSet<string> extensions, List<string> keywords)
    {
        var paths   = _config.ResolvedPaths();
        var results = new List<DiscoveryResult>();

        // If no extension filter specified, search all supported types
        bool anyExt = extensions.Count == 0;

        foreach (var searchPath in paths)
        {
            if (!Directory.Exists(searchPath)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(searchPath, "*", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var file in files)
            {
                if (results.Count >= _config.MaxResults) goto done;

                var ext  = Path.GetExtension(file);
                var name = Path.GetFileNameWithoutExtension(file);

                // Extension check
                bool extMatch = anyExt
                    ? SupportedExtensions.Contains(ext)
                    : extensions.Contains(ext);
                if (!extMatch) continue;

                // Keyword check — any keyword must appear in the filename
                if (keywords.Count > 0 &&
                    !keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    continue;

                FileInfo fi;
                try { fi = new FileInfo(file); }
                catch { continue; }

                results.Add(new DiscoveryResult(
                    file, Path.GetFileName(file), ext,
                    fi.Length, fi.LastWriteTime,
                    null, null, false, false));
            }
        }

        done:
        return results;
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
