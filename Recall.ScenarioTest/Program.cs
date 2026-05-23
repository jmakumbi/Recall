using Recall.Discovery;
using Recall.Ingestion;
using Recall.Ollama;
using Recall.Retrieval;
using Recall.Storage;

// ╔══════════════════════════════════════════════════════════════════╗
// ║              Recall — End-to-End Scenario Test                  ║
// ║  Discovery → Ingestion → Retrieval → Chat with real files       ║
// ╚══════════════════════════════════════════════════════════════════╝

Console.OutputEncoding = System.Text.Encoding.UTF8;
Heading("Recall — End-to-End Scenario Test");

// ── Config ────────────────────────────────────────────────────────────────────
var searchPaths = new[]
{
    @"D:\OneDrive\Microsoft Word Files",
    @"D:\OneDrive\Microsoft Excel files",
    @"D:\OneDrive\Microsoft Powerpoint files",
    @"D:\OneDrive\PDF Files"
};

var vec0Path = Path.GetFullPath(@"libs\vec0.dll");
var dbPath   = Path.Combine(Path.GetTempPath(), $"recall_scenario_{Guid.NewGuid():N}.db");

var discoveryConfig = new DiscoveryConfig { SearchPaths = searchPaths };
var ingestionConfig = new IngestionConfig();
var ollamaConfig    = new OllamaConfig { ChatModel = "qwen3:14b" };
var retrievalConfig = new RetrievalConfig { TopK = 5, MaxDistance = 1.0f, MaxContextChars = 6_000 };

// ── Prerequisites ─────────────────────────────────────────────────────────────
if (!File.Exists(vec0Path))
{
    Fail($"vec0.dll not found at {vec0Path}");
    return;
}

using var ollama = new OllamaClient(ollamaConfig);
bool chatReady = false;
try
{
    var health = await ollama.HealthCheckAsync();
    if (!health.EmbeddingModelReady)
    {
        Warn($"Embedding model '{ollamaConfig.EmbeddingModel}' not pulled. Run: ollama pull {ollamaConfig.EmbeddingModel}");
        return;
    }
    chatReady = health.ChatModelReady;
    Ok($"Ollama ready — embedding: {ollamaConfig.EmbeddingModel}  chat: {ollamaConfig.ChatModel} ({(chatReady ? "ready" : "NOT FOUND")})");
    if (!chatReady)
        Warn($"  Chat step will use available model or be skipped — pull with: ollama pull {ollamaConfig.ChatModel}");
    Console.WriteLine();
}
catch (OllamaUnavailableException ex) { Fail($"Ollama unavailable: {ex.Message}"); return; }

// ══════════════════════════════════════════════════════════════════════════════
// STEP 1 — Discovery
// ══════════════════════════════════════════════════════════════════════════════
Section("Step 1: Discovery");

string searchQuery = "business model";
Console.WriteLine($"  Query   : \"{searchQuery}\"");
Console.WriteLine($"  Folders : {searchPaths.Length} paths  ({searchPaths.Sum(p => Directory.Exists(p) ? new DirectoryInfo(p).EnumerateFiles("*", SearchOption.TopDirectoryOnly).Count() : 0):N0} files at top level)");
Console.WriteLine();

var discoveryService = new DiscoveryService(discoveryConfig);
var allResults       = discoveryService.Search(searchQuery).ToList();

if (allResults.Count > 0)
{
    Console.WriteLine($"  WDS found {allResults.Count} file(s):");
    Console.WriteLine();
    PrintResultTable(allResults.Take(10).ToList());
}
else
{
    Warn("  Windows Search returned 0 results (index may not cover these paths).");
    Console.WriteLine("  Falling back to direct filesystem enumeration for the test…");
    Console.WriteLine();

    // Pick a representative spread: 2 .docx, 2 .xlsx, 1 .pptx
    var picks = new List<string>();
    picks.AddRange(PickFiles(@"D:\OneDrive\Microsoft Word Files",        "*.docx", 2));
    picks.AddRange(PickFiles(@"D:\OneDrive\Microsoft Excel files",       "*.xlsx", 2));
    picks.AddRange(PickFiles(@"D:\OneDrive\Microsoft Powerpoint files",  "*.pptx", 1));

    allResults = picks.Select(p => new DiscoveryResult(
        p, Path.GetFileName(p), Path.GetExtension(p).ToLower(),
        new FileInfo(p).Length, File.GetLastWriteTime(p),
        null, null, false, false))
        .ToList();

    Console.WriteLine($"  Filesystem pick: {allResults.Count} file(s):");
    Console.WriteLine();
    PrintResultTable(allResults);
}

SectionEnd();

// ══════════════════════════════════════════════════════════════════════════════
// STEP 2 — Ingestion
// ══════════════════════════════════════════════════════════════════════════════
Section("Step 2: Ingestion (IFilter → Chunker → Embed → Store)");

var toIngest = allResults.Take(5).Select(r => r.FullPath).ToList();
Console.WriteLine($"  Ingesting {toIngest.Count} file(s) — this will call IFilter and Ollama embed…");
Console.WriteLine();

using var db   = TrackerDb.OpenAndInit(dbPath, vec0Path);
var vectors    = new VectorStore(db);
var extractor  = new IFilterExtractor(ingestionConfig);
var pipeline   = new IngestionPipeline(ingestionConfig, extractor, db, vectors, ollama);

string lastFile = string.Empty;
int    lastChk  = 0;
var    prog     = new Progress<IngestionProgress>(p =>
{
    if (p.FileName != lastFile)
    {
        if (lastFile != string.Empty) Console.WriteLine();
        Console.Write($"  [{p.FileIndex}/{p.TotalFiles}] {Trunc(p.FileName, 50),-50}");
        lastFile = p.FileName;
        lastChk  = 0;
    }
    if (p.Phase == "done")
        Console.Write($" → {p.ChunksEmitted} chunk(s)");
    else if (p.Phase is "skipped" or "error")
        Console.Write($" → {p.Phase}");
    else if (p.Phase == "embedding" && p.ChunksEmitted != lastChk)
    {
        lastChk = p.ChunksEmitted;
        Console.Write($"\r  [{p.FileIndex}/{p.TotalFiles}] {Trunc(p.FileName, 50),-50} embedding {p.ChunksEmitted}…  ");
    }
});

await pipeline.IngestAsync(toIngest, prog);
Console.WriteLine();
Console.WriteLine();

var stats = db.GetKbStats();
Ok($"  Ingestion complete: {stats.TotalFiles} file(s), {stats.TotalChunks} chunk(s) stored");
SectionEnd();

// ══════════════════════════════════════════════════════════════════════════════
// STEP 3 — Retrieval (ANN vector search)
// ══════════════════════════════════════════════════════════════════════════════
Section("Step 3: ANN Retrieval");

var retriever = new Retriever(ollama, vectors, retrievalConfig);

// Ask two different questions
var questions = new[]
{
    "What are the key elements of this document?",
    "Summarize the main points covered."
};

foreach (var question in questions)
{
    Console.WriteLine($"  Q: \"{question}\"");
    var chunks = await retriever.QueryAsync(question);
    Console.WriteLine($"     → {chunks.Count} chunk(s) retrieved");
    foreach (var c in chunks.Take(3))
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"     ▸ {c.FileName}  L2={c.Distance:F4}  ");
        Console.ResetColor();
        var snip = c.Text.Replace('\n', ' ').Trim();
        Console.WriteLine(snip.Length > 100 ? snip[..100] + "…" : snip);
    }
    Console.WriteLine();
}

SectionEnd();

// ══════════════════════════════════════════════════════════════════════════════
// STEP 4 — Chat (streaming LLM)
// ══════════════════════════════════════════════════════════════════════════════
Section("Step 4: Chat (streaming)");

// Find available chat model
var health2    = await ollama.HealthCheckAsync();
string chatModel = ollamaConfig.ChatModel;

if (!health2.ChatModelReady)
{
    // Pick any available non-embedding model
    chatModel = health2.InstalledModels
        .FirstOrDefault(m => !m.StartsWith("nomic") && !m.StartsWith("mxbai"))
        ?? string.Empty;
    if (string.IsNullOrEmpty(chatModel))
    {
        Warn("  No usable chat model found. Skipping chat step.");
        goto Done;
    }
    Warn($"  '{ollamaConfig.ChatModel}' not found — using '{chatModel}' instead");
}

{
    // Use the last question's chunks for context
    var chatQuestion = "What are the main topics and key points covered in these documents?";
    var chunks       = await retriever.QueryAsync(chatQuestion);
    var context      = retriever.AssembleContext(chunks);

    // Build a temporary OllamaClient with the actual chat model if it differs
    var chatCfg    = new OllamaConfig { EmbeddingModel = ollamaConfig.EmbeddingModel, ChatModel = chatModel };
    using var chat = new OllamaClient(chatCfg);

    var messages = new List<ChatMessage>
    {
        ChatMessage.System(OllamaClient.SystemPrompt +
            (context.Length > 0
                ? $"\n\nContext from user's files:\n{context}"
                : "\n\nNo relevant documents found.")),
        ChatMessage.User(chatQuestion)
    };

    Console.WriteLine($"  Q: \"{chatQuestion}\"");
    Console.WriteLine($"  Context: {context.Length:N0} chars from {chunks.Select(c => c.FileName).Distinct().Count()} file(s)");
    Console.WriteLine($"  Model:   {chatModel}");
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Blue;
    Console.Write("  ▸ recall: ");
    Console.ResetColor();

    await foreach (var token in chat.ChatAsync(messages))
        Console.Write(token);

    Console.WriteLine();
    Console.WriteLine();

    var sources = chunks.Select(c => c.FileName).Distinct().ToList();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"  [Sources: {string.Join(", ", sources)}]");
    Console.ResetColor();
}

Done:
SectionEnd();

// ── Cleanup ───────────────────────────────────────────────────────────────────
db.Dispose();
try { File.Delete(dbPath); } catch { }

Console.WriteLine();
Ok("Scenario test complete.");

// ── Helpers ───────────────────────────────────────────────────────────────────

static void Heading(string t)
{
    var bar = new string('═', t.Length + 4);
    Console.WriteLine($"╔{bar}╗");
    Console.WriteLine($"║  {t}  ║");
    Console.WriteLine($"╚{bar}╝");
    Console.WriteLine();
}
static void Section(string t)   => Console.WriteLine($"┌─ {t} {new string('─', Math.Max(0, 55 - t.Length))}┐");
static void SectionEnd()        => Console.WriteLine($"└{'─',59}┘\n");
static void Ok(string s)        { Console.ForegroundColor = ConsoleColor.Green;  Console.WriteLine(s); Console.ResetColor(); }
static void Warn(string s)      { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(s); Console.ResetColor(); }
static void Fail(string s)      { Console.ForegroundColor = ConsoleColor.Red;    Console.WriteLine(s); Console.ResetColor(); }
static string Trunc(string s, int n) => s.Length > n ? s[..(n-1)] + "…" : s;

static IEnumerable<string> PickFiles(string dir, string pattern, int count) =>
    Directory.Exists(dir)
        ? Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly)
              .Where(f => { var fi = new FileInfo(f); return fi.Length is > 5_000 and < 2_000_000; })
              .Take(count)
        : [];

static void PrintResultTable(List<DiscoveryResult> results)
{
    foreach (var (r, i) in results.Select((r, i) => (r, i + 1)))
    {
        var status = r.AlreadyIngested ? (r.IsStale ? "[STALE]" : "[IN KB]") : "[NEW  ]";
        var colour = r.AlreadyIngested ? (r.IsStale ? ConsoleColor.Yellow : ConsoleColor.Green) : ConsoleColor.Gray;
        Console.Write($"  {i,2}. ");
        Console.ForegroundColor = colour;
        Console.Write($"{status} ");
        Console.ResetColor();
        Console.WriteLine($"{r.FileName}  ({r.SizeBytes / 1024} KB)");
    }
    Console.WriteLine();
}
