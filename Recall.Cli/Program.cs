using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Recall.Cli;
using Recall.Discovery;
using Recall.Ingestion;
using Recall.Ollama;
using Recall.Retrieval;
using Recall.Storage;
using Spectre.Console;

// ── Config loading ────────────────────────────────────────────────────────────

var exeDir       = AppContext.BaseDirectory;
var settingsPath = Path.Combine(exeDir, "appsettings.json");

// Fall back to repo root when running from output/bin directory (dev experience)
if (!File.Exists(settingsPath))
    settingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");

var configuration = new ConfigurationBuilder()
    .AddJsonFile(settingsPath, optional: true)
    .Build();

string Expand(string? s) =>
    Environment.ExpandEnvironmentVariables(s ?? string.Empty);

var recallSection     = configuration.GetSection("Recall");
var ollamaSection     = configuration.GetSection("Ollama");
var ingestionSection  = configuration.GetSection("Ingestion");
var retrievalSection  = configuration.GetSection("Retrieval");

var rawDbPath         = recallSection["DbPath"]            ?? @"%APPDATA%\recall\recall.db";
var vec0DllPath       = Path.GetFullPath(recallSection["Vec0DllPath"]        ?? @"libs\vec0.dll");
var everythingDllPath = Path.GetFullPath(recallSection["EverythingDllPath"]  ?? @"libs\Everything64.dll");
var rawSearchPaths    = recallSection.GetSection("SearchPaths").GetChildren()
    .Select(c => c.Value ?? string.Empty)
    .Where(v => !string.IsNullOrEmpty(v))
    .ToArray();
var resolvedPaths     = rawSearchPaths
    .Select(Expand)
    .Where(Directory.Exists)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

var dbPath = Expand(rawDbPath);
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

// ── AppConfig ─────────────────────────────────────────────────────────────────

var appConfig = new AppConfig(settingsPath)
{
    SearchPaths      = resolvedPaths,
    DbPath           = dbPath,
    Vec0DllPath      = vec0DllPath,
    EverythingDllPath = everythingDllPath
};

// ── Ollama config from appsettings ────────────────────────────────────────────

var ollamaConfig = new OllamaConfig
{
    BaseUrl           = ollamaSection["BaseUrl"]      ?? "http://localhost:11434",
    EmbeddingModel    = ollamaSection["EmbeddingModel"] ?? "nomic-embed-text",
    ChatModel         = ollamaSection["ChatModel"]    ?? "qwen3:8b",
    ChatContextWindow = int.TryParse(ollamaSection["ChatContextWindow"], out var ctx) ? ctx : 8192
};

var ingestionConfig = new IngestionConfig
{
    ChunkSize                = int.TryParse(ingestionSection["ChunkSize"],                out var cs)  ? cs  : 512,
    ChunkOverlap             = int.TryParse(ingestionSection["ChunkOverlap"],             out var co)  ? co  : 100,
    MaxExtractedCharsPerFile = int.TryParse(ingestionSection["MaxExtractedCharsPerFile"], out var mx)  ? mx  : 500_000
};

var retrievalConfig = new RetrievalConfig
{
    TopK            = int.TryParse(retrievalSection["TopK"],            out var tk)  ? tk  : 5,
    MaxDistance     = float.TryParse(retrievalSection["MaxDistance"],   out var md)  ? md  : 1.0f,
    MaxContextChars = int.TryParse(retrievalSection["MaxContextChars"], out var mcc) ? mcc : 6_000
};

// ── DI wiring ─────────────────────────────────────────────────────────────────

var services = new ServiceCollection();

services.AddSingleton(appConfig);
services.AddSingleton(ollamaConfig);
services.AddSingleton(ingestionConfig);
services.AddSingleton(retrievalConfig);

services.AddSingleton(_ => TrackerDb.OpenAndInit(dbPath, vec0DllPath));
services.AddSingleton(sp => new VectorStore(sp.GetRequiredService<TrackerDb>()));
services.AddSingleton(sp => new OllamaClient(sp.GetRequiredService<OllamaConfig>()));

var discoveryConfig = new DiscoveryConfig { SearchPaths = resolvedPaths, EverythingDllPath = everythingDllPath };
services.AddSingleton(discoveryConfig);
services.AddSingleton(sp => new DiscoveryService(
    sp.GetRequiredService<DiscoveryConfig>(),
    sp.GetRequiredService<TrackerDb>()));

services.AddSingleton(sp => new IFilterExtractor(sp.GetRequiredService<IngestionConfig>()));
services.AddSingleton(sp => new IngestionPipeline(
    sp.GetRequiredService<IngestionConfig>(),
    sp.GetRequiredService<IFilterExtractor>(),
    sp.GetRequiredService<TrackerDb>(),
    sp.GetRequiredService<VectorStore>(),
    sp.GetRequiredService<OllamaClient>()));

services.AddSingleton(sp => new Retriever(
    sp.GetRequiredService<OllamaClient>(),
    sp.GetRequiredService<VectorStore>(),
    sp.GetRequiredService<RetrievalConfig>()));

services.AddSingleton<IntentClassifier>();
services.AddSingleton(sp => new Repl(
    sp.GetRequiredService<TrackerDb>(),
    sp.GetRequiredService<VectorStore>(),
    sp.GetRequiredService<OllamaClient>(),
    sp.GetRequiredService<DiscoveryService>(),
    sp.GetRequiredService<IngestionPipeline>(),
    sp.GetRequiredService<Retriever>(),
    sp.GetRequiredService<IntentClassifier>(),
    sp.GetRequiredService<AppConfig>()));

var provider = services.BuildServiceProvider();

// ── Startup health check ───────────────────────────────────────────────────────

using var cts    = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var ollama = provider.GetRequiredService<OllamaClient>();

OllamaHealthResult? health = null;
try
{
    health = await ollama.HealthCheckAsync(cts.Token);
}
catch (OllamaUnavailableException ex)
{
    AnsiConsole.MarkupLine($"[red]⚠ {Markup.Escape(ex.Message)}[/]");
    AnsiConsole.MarkupLine("[dim]Recall will run in read-only mode (no embedding or chat).[/]");
    AnsiConsole.WriteLine();
}

if (health is not null)
{
    if (!health.EmbeddingModelReady)
        AnsiConsole.MarkupLine(
            $"[yellow]⚠ Embedding model '[bold]{Markup.Escape(ollamaConfig.EmbeddingModel)}[/]' not found.[/] " +
            $"Pull it with: [bold]ollama pull {Markup.Escape(ollamaConfig.EmbeddingModel)}[/]");

    if (!health.ChatModelReady)
        AnsiConsole.MarkupLine(
            $"[yellow]⚠ Chat model '[bold]{Markup.Escape(ollamaConfig.ChatModel)}[/]' not found.[/] " +
            $"Pull it with: [bold]ollama pull {Markup.Escape(ollamaConfig.ChatModel)}[/]");
}

// ── Run REPL ──────────────────────────────────────────────────────────────────

var repl = provider.GetRequiredService<Repl>();
await repl.RunAsync(cts.Token);

// ── Cleanup ───────────────────────────────────────────────────────────────────

provider.GetRequiredService<TrackerDb>().Dispose();
provider.GetRequiredService<OllamaClient>().Dispose();
