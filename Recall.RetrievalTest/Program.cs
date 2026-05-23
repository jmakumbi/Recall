using Recall.Ingestion;
using Recall.Ollama;
using Recall.Retrieval;
using Recall.Storage;

Console.WriteLine("=== Recall.Retrieval smoke test ===\n");

var ingestionConfig = new IngestionConfig();
var ollamaConfig    = new OllamaConfig();
var retrievalConfig = new RetrievalConfig();
var vec0Path        = Path.GetFullPath(@"libs\vec0.dll");
var dbPath          = Path.Combine(Path.GetTempPath(), $"recall_retrieval_test_{Guid.NewGuid():N}.db");

// ── Prerequisites ─────────────────────────────────────────────────────────────
if (!File.Exists(vec0Path))
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"  SKIP: vec0.dll not found at {vec0Path}");
    Console.ResetColor();
    return;
}

using var ollama = new OllamaClient(ollamaConfig);
OllamaHealthResult health;
try   { health = await ollama.HealthCheckAsync(); }
catch (OllamaUnavailableException ex)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"  SKIP: {ex.Message}");
    Console.ResetColor();
    return;
}
if (!health.EmbeddingModelReady)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"  SKIP: '{ollamaConfig.EmbeddingModel}' not pulled.");
    Console.ResetColor();
    return;
}

// ── Build a small KB with two synthetic topic files ───────────────────────────
Console.WriteLine("--- Seeding KB with two synthetic files ---");

var file1 = Path.Combine(Path.GetTempPath(), $"recall_ret_test_a_{Guid.NewGuid():N}.txt");
var file2 = Path.Combine(Path.GetTempPath(), $"recall_ret_test_b_{Guid.NewGuid():N}.txt");

File.WriteAllText(file1, string.Join('\n',
    Enumerable.Range(1, 120).Select(_ =>
        "Recall is a Windows RAG CLI that lets users query personal files using " +
        "local Ollama LLMs without cloud dependencies. It uses IFilter for text extraction, " +
        "sqlite-vec for vector storage, and Spectre.Console for a rich terminal UI.")));

File.WriteAllText(file2, string.Join('\n',
    Enumerable.Range(1, 120).Select(_ =>
        "The solar system contains eight planets: Mercury, Venus, Earth, Mars, Jupiter, " +
        "Saturn, Uranus, and Neptune. The Sun accounts for 99.86% of the total mass. " +
        "Jupiter is the largest planet, with a mass greater than all other planets combined.")));

try
{
    using var db  = TrackerDb.OpenAndInit(dbPath, vec0Path);
    var vectors   = new VectorStore(db);
    var extractor = new PlainTextExtractor(ingestionConfig);
    var pipeline  = new IngestionPipeline(ingestionConfig, extractor, db, vectors, ollama);
    var retriever = new Retriever(ollama, vectors, retrievalConfig);

    await pipeline.IngestAsync([file1, file2], null);
    var stats = db.GetKbStats();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  OK: KB seeded — {stats.TotalFiles} file(s), {stats.TotalChunks} chunk(s)");
    Console.ResetColor();
    Console.WriteLine();

    // ── Test 1: Retriever.QueryAsync ──────────────────────────────────────────
    Console.WriteLine("--- Retriever.QueryAsync ---");

    var ragQuery  = "What is Recall and how does it store vectors?";
    var ragChunks = await retriever.QueryAsync(ragQuery);

    Console.WriteLine($"  Query : \"{ragQuery}\"");
    Console.WriteLine($"  Hits  : {ragChunks.Count}");

    if (ragChunks.Count > 0)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  OK: top result from '{ragChunks[0].FileName}' (L2={ragChunks[0].Distance:F4})");
        Console.ResetColor();

        // Validate the closest result comes from file1 (Recall topic)
        bool fromCorrectFile = Path.GetFullPath(ragChunks[0].FilePath)
            .Equals(Path.GetFullPath(file1), StringComparison.OrdinalIgnoreCase);
        Console.ForegroundColor = fromCorrectFile ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine(fromCorrectFile
            ? "  OK: closest chunk is from the Recall topic file"
            : "  WARN: closest chunk is NOT from the Recall topic file — check embedding quality");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  FAIL: no chunks returned — check MaxDistance threshold");
        Console.ResetColor();
    }
    Console.WriteLine();

    // ── Test 2: Retriever.AssembleContext ─────────────────────────────────────
    Console.WriteLine("--- Retriever.AssembleContext ---");

    var context = retriever.AssembleContext(ragChunks);
    Console.WriteLine($"  Context length : {context.Length:N0} chars");

    bool hasSource = context.Contains("[Source:");
    Console.ForegroundColor = (hasSource && context.Length > 0) ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine(hasSource && context.Length > 0 ? "  OK: context has [Source:] headers" : "  FAIL: malformed context");
    Console.ResetColor();

    // Show a preview
    var preview = context.Length > 200 ? context[..200] + "..." : context;
    Console.WriteLine($"\n  Preview:\n{string.Join('\n', preview.Split('\n').Select(l => "    " + l))}");
    Console.WriteLine();

    // ── Test 3: Context character cap ─────────────────────────────────────────
    Console.WriteLine("--- Context character cap ---");
    var smallConfig    = new RetrievalConfig { TopK = 10, MaxContextChars = 500 };
    var smallRetriever = new Retriever(ollama, vectors, smallConfig);
    var allChunks      = await smallRetriever.QueryAsync(ragQuery);
    var capped         = smallRetriever.AssembleContext(allChunks);
    Console.ForegroundColor = capped.Length <= 500 ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine(capped.Length <= 500
        ? $"  OK: capped context = {capped.Length} chars (≤ 500)"
        : $"  FAIL: context exceeded cap at {capped.Length} chars");
    Console.ResetColor();
}
finally
{
    try { File.Delete(file1); } catch { }
    try { File.Delete(file2); } catch { }
    try { File.Delete(dbPath); } catch { }
}

Console.WriteLine("\nRetrieval smoke test complete.");

// ── Helpers ───────────────────────────────────────────────────────────────────

sealed class PlainTextExtractor(IngestionConfig config) : IFilterExtractor(config)
{
    public override string? Extract(string filePath)
    {
        try
        {
            var text = File.ReadAllText(filePath);
            return text.Length > config.MaxExtractedCharsPerFile
                ? text[..config.MaxExtractedCharsPerFile]
                : text;
        }
        catch { return null; }
    }
}
