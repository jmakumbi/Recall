using Recall.Discovery;

var query = args.Length > 0 ? string.Join(' ', args) : "readme";

var config = new DiscoveryConfig
{
    EverythingDllPath = Path.GetFullPath(@"libs\Everything64.dll"),
    SearchPaths       = [@"%USERPROFILE%"],
    MaxResults        = 20
};

Console.WriteLine("=== Recall.Discovery smoke test ===");
Console.WriteLine($"Query  : \"{query}\"");
Console.WriteLine($"Paths  : {string.Join(", ", config.ResolvedPaths())}");
Console.WriteLine($"DLL    : {config.EverythingDllPath} (exists: {File.Exists(config.EverythingDllPath)})");
Console.WriteLine();

var service = new DiscoveryService(config, tracker: null);

// ── Everything ────────────────────────────────────────────────────────────
Console.WriteLine("--- Everything results ---");
var everythingClient = new EverythingClient(config);
var everythingHits   = everythingClient.Search(query);

if (everythingHits.Count == 0)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("  (no results — is Everything running?)");
    Console.ResetColor();
}
else
{
    foreach (var r in everythingHits.Take(5))
        Console.WriteLine($"  {r.SizeBytes,8:N0} B  {r.LastModified:yyyy-MM-dd}  {r.FileName}");
    if (everythingHits.Count > 5)
        Console.WriteLine($"  ... and {everythingHits.Count - 5} more");
}

Console.WriteLine();

// ── Windows Search ────────────────────────────────────────────────────────
Console.WriteLine("--- Windows Search results ---");
var wdsClient = new WindowsSearchClient(config);
var wdsHits   = wdsClient.Search(query);

if (wdsHits.Count == 0)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("  (no results)");
    Console.ResetColor();
}
else
{
    foreach (var r in wdsHits.Take(5))
        Console.WriteLine($"  [{r.WdsKind ?? "?"}]  {r.FileName}  {(r.WdsSnippet != null ? "(snippet)" : "")}");
    if (wdsHits.Count > 5)
        Console.WriteLine($"  ... and {wdsHits.Count - 5} more");
}

Console.WriteLine();

// ── Merged ────────────────────────────────────────────────────────────────
Console.WriteLine("--- Merged results (Everything canonical + WDS enriched) ---");
var merged = service.Search(query);
Console.WriteLine($"  Total: {merged.Count} unique file(s)");

foreach (var r in merged.Take(10))
{
    var kb      = r.AlreadyIngested ? (r.IsStale ? "[STALE]" : "[KB]   ") : "[new]  ";
    var snippet = r.WdsSnippet is not null ? " ✓snippet" : "";
    var kind    = r.WdsKind    is not null ? $" [{r.WdsKind}]" : "";
    Console.WriteLine($"  {kb}  {r.FileName}{kind}{snippet}");
}

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("\nDiscovery smoke test complete.");
Console.ResetColor();
