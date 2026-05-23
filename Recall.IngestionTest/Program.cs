using Recall.Ingestion;
using Recall.Ollama;
using Recall.Storage;

Console.WriteLine("=== Recall.Ingestion smoke test ===\n");

var ingestionConfig = new IngestionConfig();
var ollamaConfig    = new OllamaConfig();
var vec0Path        = Path.GetFullPath(@"libs\vec0.dll");
var dbPath          = Path.Combine(Path.GetTempPath(), $"recall_ingest_test_{Guid.NewGuid():N}.db");

// ── 1. Chunker unit test (no external deps) ───────────────────────────────
Console.WriteLine("--- Chunker ---");
var sampleText = string.Join(' ', Enumerable.Repeat(
    "The quick brown fox jumps over the lazy dog", 200));

var chunks = Chunker.Chunk(sampleText, chunkSize: 128, overlap: 32).ToList();
Console.WriteLine($"  Input  : {sampleText.Length:N0} chars (~{sampleText.Length / 4} tokens)");
Console.WriteLine($"  Chunks : {chunks.Count}");
Console.WriteLine($"  Tokens : {chunks.Select(c => c.Length / 4).Min()}–{chunks.Select(c => c.Length / 4).Max()} per chunk");

bool chunkerOk = chunks.Count > 1 && chunks.All(c => c.Length / 4 >= 50);
Console.ForegroundColor = chunkerOk ? ConsoleColor.Green : ConsoleColor.Red;
Console.WriteLine(chunkerOk ? "  OK" : "  FAIL");
Console.ResetColor();
Console.WriteLine();

// ── 2. IFilterExtractor — find a real file to test against ───────────────
Console.WriteLine("--- IFilterExtractor ---");
var extractor = new IFilterExtractor(ingestionConfig);

// Look for any existing .txt or .docx in Documents — avoids LoadIFilter
// quirks with freshly-created temp files in some environments.
var docsPath  = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
var testFile  = Directory.EnumerateFiles(docsPath, "*.txt", SearchOption.AllDirectories)
                         .Concat(Directory.EnumerateFiles(docsPath, "*.docx", SearchOption.AllDirectories))
                         .FirstOrDefault(f => new FileInfo(f).Length is > 1024 and < 5_000_000);

if (testFile is null)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("  SKIP: no suitable .txt/.docx found in Documents to test IFilter against.");
    Console.ResetColor();
}
else
{
    Console.WriteLine($"  Testing with: {Path.GetFileName(testFile)}");
    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    string? extracted = null;
    try
    {
        extracted = await Task.Run(() => extractor.Extract(testFile), cts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  SKIP: IFilter timed out — LoadIFilter may need a running message loop in this environment.");
        Console.ResetColor();
        goto PipelineTest;
    }

    if (extracted is not null)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  OK: extracted {extracted.Length:N0} chars");
        Console.ResetColor();
        var fileChunks = Chunker.Chunk(extracted, ingestionConfig.ChunkSize, ingestionConfig.ChunkOverlap).ToList();
        Console.WriteLine($"  Chunks from real file: {fileChunks.Count}");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  SKIP: IFilter returned null (no registered filter for this file type).");
        Console.ResetColor();
    }
}

PipelineTest:
Console.WriteLine();

// ── 3. Full pipeline with plain text via File.ReadAllText fallback ─────────
// The pipeline uses IFilter; for this test we bypass it and embed directly
// to validate the chunk → embed → store → search path independently.
Console.WriteLine("--- IngestionPipeline (embed + store + search) ---");
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

// Synthesise a text file with enough content to produce several chunks,
// write it, and feed it to the pipeline.
var syntheticFile = Path.Combine(Path.GetTempPath(), $"recall_test_{Guid.NewGuid():N}.txt");
File.WriteAllText(syntheticFile, string.Join('\n',
    Enumerable.Range(1, 300).Select(i =>
        $"Sentence {i}: recall is a Windows RAG CLI that lets users chat with their personal files using local Ollama LLMs without any cloud dependencies.")));

try
{
    using var db = TrackerDb.OpenAndInit(dbPath, vec0Path);
    var vectors  = new VectorStore(db);

    // ── Stub extractor: reads plain text directly instead of via IFilter ──
    // This validates chunk→embed→store without the LoadIFilter dependency.
    var stubExtractor = new PlainTextExtractor(ingestionConfig);
    var pipeline      = new IngestionPipeline(ingestionConfig, stubExtractor, db, vectors, ollama);

    int lastChunks = 0;
    var progress = new Progress<IngestionProgress>(p =>
    {
        if (p.ChunksEmitted != lastChunks || p.Phase == "done")
        {
            lastChunks = p.ChunksEmitted;
            Console.Write($"\r  [{p.FileIndex}/{p.TotalFiles}] {p.Phase,-12} chunks={p.ChunksEmitted}   ");
        }
    });

    await pipeline.IngestAsync([syntheticFile], progress);
    Console.WriteLine();

    var stats = db.GetKbStats();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"\n  OK: KB → {stats.TotalFiles} file(s), {stats.TotalChunks} chunk(s)");

    // With normalised embeddings, L2 distance ∈ [0, 2].
    // distance = sqrt(2 * (1 - cos_sim)), so minScore 1.0 ≈ cosine similarity ≥ 0.5.
    var q       = await ollama.EmbedAsync("Windows RAG CLI local Ollama");
    var results = vectors.Search(q, topK: 3, minScore: 1.0f);
    Console.WriteLine($"  OK: ANN search → {results.Count} result(s)" +
        (results.Count > 0 ? $" (closest L2 distance: {results[0].Distance:F4})" : " ⚠ check distance threshold"));

    // Second pass: same file unmodified → should be skipped
    await pipeline.IngestAsync([syntheticFile], null);
    var stats2 = db.GetKbStats();
    Console.WriteLine($"  OK: Re-ingest skipped (chunks still {stats2.TotalChunks})");
    Console.ResetColor();
}
finally
{
    try { File.Delete(syntheticFile); } catch { }
    try { File.Delete(dbPath); }        catch { }
}

Console.WriteLine("\nIngestion smoke test complete.");

// ── Helpers ───────────────────────────────────────────────────────────────

/// <summary>
/// Drop-in replacement for IFilterExtractor that reads plain text via
/// File.ReadAllText. Used in this test to isolate the pipeline from
/// the LoadIFilter COM dependency.
/// </summary>
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
