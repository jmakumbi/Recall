# Claude Code Prompt — `recall` Windows RAG CLI

## Context

Build a self-contained Windows CLI application called **`recall`** that lets a user chat with their personal corpus of files using natural language. The tool uses Windows-native APIs for file discovery and text extraction, a locally-running Ollama instance for embeddings and chat, and sqlite-vec for persistent vector storage — all with zero external service dependencies beyond Ollama.

---

## Project Identity

- **Solution name:** `Recall`
- **Primary executable project:** `Recall.Cli`
- **Target framework:** `net8.0-windows` (Windows-only, required for COM and OLE DB interop)
- **Language:** C# 12
- **Nullable:** enabled
- **Self-contained publish:** yes, `win-x64`
- **Author:** James S. K. Makumbi

---

## Solution Structure

```
Recall/
├── Recall.sln
├── install.ps1                         ← Bootstrap installer script
├── README.md
│
├── Recall.Cli/                         ← Console app, Spectre.Console REPL
│   ├── Program.cs
│   ├── Repl.cs                         ← Main chat/command loop
│   ├── IntentClassifier.cs             ← Classify user input: search | chat | command
│   └── Recall.Cli.csproj
│
├── Recall.Discovery/                   ← File discovery layer
│   ├── EverythingClient.cs             ← P/Invoke wrapper for Everything64.dll
│   ├── WindowsSearchClient.cs          ← WDS OLE DB query helper
│   ├── DiscoveryResult.cs              ← Unified result model
│   └── Recall.Discovery.csproj
│
├── Recall.Ingestion/                   ← Extraction + embedding pipeline
│   ├── IFilterExtractor.cs             ← COM interop IFilter text extraction
│   ├── Chunker.cs                      ← Sliding window chunker
│   ├── IngestionPipeline.cs            ← Orchestrates extract → chunk → embed → store
│   └── Recall.Ingestion.csproj
│
├── Recall.Retrieval/                   ← Vector search + context assembly
│   ├── VectorStore.cs                  ← sqlite-vec operations
│   ├── Retriever.cs                    ← Query embedding + ANN search + metadata filter
│   └── Recall.Retrieval.csproj
│
├── Recall.Ollama/                      ← Ollama HTTP client
│   ├── OllamaClient.cs                 ← Embed() and Chat() methods
│   ├── OllamaModels.cs                 ← Request/response DTOs
│   └── Recall.Ollama.csproj
│
├── Recall.Storage/                     ← SQLite tracker + vector store init
│   ├── TrackerDb.cs                    ← File ingestion tracker, KB stats
│   ├── Schema.cs                       ← DDL: ingested_files, conversations, kb_stats
│   └── Recall.Storage.csproj
│
└── libs/                               ← Bundled native DLLs (not NuGet)
    ├── Everything64.dll                ← Voidtools redistributable
    └── vec0.dll                        ← sqlite-vec extension
```

---

## NuGet Dependencies

### Recall.Cli
- `Spectre.Console` — REPL rendering, tables, progress bars, prompts
- `Spectre.Console.Cli` — command routing (optional, start without if REPL-only)
- `Microsoft.Extensions.DependencyInjection`
- `Microsoft.Extensions.Configuration.Json`

### Recall.Storage
- `Microsoft.Data.Sqlite` — bundles `sqlite3.dll`, zero external dependency

### Recall.Discovery
- `System.Data.OleDb` — WDS OLE DB access (Windows-only NuGet package)

### Recall.Ollama
- `System.Text.Json`

### All projects
- No third-party parsers. No iTextSharp, Open XML SDK, PdfPig, or similar.

---

## Configuration

`appsettings.json` placed alongside `recall.exe`:

```json
{
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "EmbeddingModel": "nomic-embed-text",
    "ChatModel": "qwen3:8b",
    "ChatContextWindow": 8192
  },
  "Storage": {
    "DbPath": "%APPDATA%\\Recall\\recall.db"
  },
  "Ingestion": {
    "ChunkSize": 512,
    "ChunkOverlap": 100,
    "MaxExtractedCharsPerFile": 500000
  },
  "Discovery": {
    "EverythingDllPath": "libs\\Everything64.dll",
    "DefaultSearchScope": "%USERPROFILE%"
  },
  "Retrieval": {
    "TopK": 10,
    "MinSimilarityScore": 0.3
  }
}
```

Expand `%APPDATA%` and `%USERPROFILE%` at runtime using `Environment.ExpandEnvironmentVariables()`.

---

## Everything64.dll — P/Invoke Wrapper

### Key API Functions to Wrap

```
Everything_SetSearchW(string search)
Everything_SetRequestFlags(uint flags)
Everything_SetMax(uint max)
Everything_QueryW(bool wait) → bool
Everything_GetNumResults() → uint
Everything_GetResultFullPathNameW(uint index, StringBuilder buf, uint bufSize)
Everything_GetResultDateModified(uint index, out FILETIME ft) → bool
Everything_GetResultSize(uint index, out long size) → bool
Everything_CleanUp()
Everything_GetLastError() → uint
```

### Request Flags

Define constants:
- `EVERYTHING_REQUEST_FILE_NAME = 0x00000001`
- `EVERYTHING_REQUEST_PATH = 0x00000002`
- `EVERYTHING_REQUEST_DATE_MODIFIED = 0x00000010`
- `EVERYTHING_REQUEST_SIZE = 0x00000020`

### DLL Loading

Load via `NativeLibrary.Load(absolutePath)` using the configured `EverythingDllPath`. Resolve the full path before loading. Do not assume `Everything64.dll` is on PATH.

### Fallback Behaviour

If Everything is not running as a service, the DLL builds its own index. Surface this to the user as a one-time warning: `"Everything service not detected. First search may be slower while index builds."` Do not fail.

### Result Model (`DiscoveryResult`)

```csharp
public record DiscoveryResult(
    string FullPath,
    string FileName,
    string Extension,
    long SizeBytes,
    DateTime LastModified,
    string? WdsSnippet,        // populated by WindowsSearchClient, nullable
    string? WdsKind,           // e.g. "document", "email", "spreadsheet"
    bool AlreadyIngested,      // populated by TrackerDb lookup
    bool IsStale               // ingested but file modified since
);
```

---

## Windows Search (WDS) — OLE DB Query Helper

### Connection String

```
Provider=Search.CollatorDSO.1;Extended Properties='Application=Windows'
```

### Query Template

```sql
SELECT
    System.ItemPathDisplay,
    System.Search.AutoSummary,
    System.Kind,
    System.Author,
    System.Keywords,
    System.DateModified
FROM SystemIndex
WHERE CONTAINS(*, @query)
  AND System.ItemPathDisplay LIKE @scope
ORDER BY System.DateModified DESC
```

Use parameterised `OleDbCommand` with `AddWithValue`. Limit results to 50 via `FETCH FIRST 50 ROWS ONLY`.

### Merge With Everything Results

After running both queries, merge on `FullPath`. Everything results are canonical (MFT is authoritative for path/size/date). WDS results enrich with `AutoSummary` and `Kind` where path matches. Unmatched WDS results are appended. De-duplicate by normalised `FullPath` (lowercased, normalised separators).

---

## IFilter COM Interop — Text Extraction

### COM Interfaces to Define

Define `IFilter` and `IFilterChunk` via `[ComImport]` with correct GUIDs. Do not add any reference assembly — declare them inline.

Key methods:
- `IFilter.Init(uint grfFlags, uint cAttributes, IntPtr aAttributes, out uint pdwFlags)`
- `IFilter.GetChunk(out STAT_CHUNK pStat)`  
- `IFilter.GetText(ref uint pcwcBuffer, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder awcBuffer)`
- `IFilter.GetValue(out IntPtr ppPropValue)`

### LoadIFilter

P/Invoke `LoadIFilter` from `query.dll`:
```
[DllImport("query.dll", CharSet = CharSet.Unicode)]
static extern int LoadIFilter(string pwcsPath, IntPtr pUnkOuter, out IFilter ppIUnk);
```

### Extraction Logic

```
LoadIFilter(filePath)
loop:
  GetChunk() → if S_OK, check CHUNKSTATE for text chunks
  GetText() in a loop until FILTER_E_NO_MORE_TEXT
  Accumulate text, stop at MaxExtractedCharsPerFile
Release COM object
Return extracted string
```

### Error Handling

- `FILTER_E_NO_FILTER_FOR_EXT` (0x80004005 variant) → log and return null. Do not throw.
- `FILTER_E_ACCESS` → log permission error, return null.
- Any other HRESULT failure → log and return null.
- Always release COM objects in finally blocks.
- Never crash the CLI on extraction failure.

---

## Chunker

Sliding window over extracted text string.

```csharp
public static IEnumerable<string> Chunk(string text, int chunkSize, int overlap)
```

- Split on word boundaries, not character boundaries.
- Approximate `chunkSize` in tokens using `text.Length / 4` heuristic (good enough for Latin scripts).
- Overlap = last N tokens of previous chunk prepended to next chunk.
- Minimum chunk size: 50 tokens. Discard shorter trailing chunks.
- Return `IEnumerable<string>` — do not materialise entire list if file is large.

---

## Ollama HTTP Client

### Base URL

Configurable via `appsettings.json`. Default `http://localhost:11434`.

### Embed Method

```
POST /api/embeddings
{ "model": "nomic-embed-text", "prompt": "<chunk text>" }
→ { "embedding": [float array] }
```

Return `float[]`. Throw `OllamaUnavailableException` (custom) if connection refused. This surfaces clearly in the REPL as `"Ollama is not running. Start it with: ollama serve"`.

### Chat Method

Streaming chat:
```
POST /api/chat
{
  "model": "qwen3:8b",
  "stream": true,
  "messages": [ { "role": "...", "content": "..." } ]
}
```

Use `HttpClient` with `ResponseHeadersRead` and stream response lines. Yield tokens as they arrive. Spectre.Console renders them live.

### System Prompt for Chat

```
You are a personal knowledge assistant. You answer questions strictly based on the provided context from the user's own files. If the context does not contain enough information to answer, say so clearly. Do not speculate beyond what the context supports. When referencing information, mention the source file name.
```

### Health Check

`GET /api/tags` → verify `nomic-embed-text` and `qwen3:8b` are present in the model list. Run on startup and warn if either is missing with the pull command to fix it.

---

## sqlite-vec Integration

### Loading the Extension

```csharp
connection.EnableExtensions();
connection.LoadExtension("libs\\vec0"); // relative to exe, no .dll suffix on Windows
```

Do this once after opening the `SqliteConnection`. Resolve absolute path before calling.

### Schema (Schema.cs)

```sql
-- File ingestion tracker
CREATE TABLE IF NOT EXISTS ingested_files (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    path        TEXT NOT NULL UNIQUE,
    size_bytes  INTEGER NOT NULL,
    last_modified TEXT NOT NULL,          -- ISO 8601
    chunk_count INTEGER NOT NULL DEFAULT 0,
    ingested_at TEXT NOT NULL,            -- ISO 8601
    wds_kind    TEXT,
    vec_row_ids TEXT NOT NULL DEFAULT '[]' -- JSON array of rowids in vec_chunks
);

-- Vector chunk store (sqlite-vec virtual table)
CREATE VIRTUAL TABLE IF NOT EXISTS vec_chunks USING vec0(
    embedding float[768]                  -- nomic-embed-text dimension
);

-- Chunk text store (parallel to vec_chunks, rowid-aligned)
CREATE TABLE IF NOT EXISTS chunks (
    rowid       INTEGER PRIMARY KEY,      -- matches vec_chunks rowid
    file_id     INTEGER NOT NULL REFERENCES ingested_files(id),
    chunk_index INTEGER NOT NULL,
    text        TEXT NOT NULL
);

-- Conversation history
CREATE TABLE IF NOT EXISTS conversations (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    started_at  TEXT NOT NULL,
    messages    TEXT NOT NULL DEFAULT '[]' -- JSON array of {role, content}
);

-- KB stats (single row)
CREATE TABLE IF NOT EXISTS kb_stats (
    id              INTEGER PRIMARY KEY CHECK (id = 1),
    total_files     INTEGER NOT NULL DEFAULT 0,
    total_chunks    INTEGER NOT NULL DEFAULT 0,
    last_updated    TEXT
);
INSERT OR IGNORE INTO kb_stats (id, total_files, total_chunks) VALUES (1, 0, 0);
```

### Vector Insert

```sql
INSERT INTO vec_chunks (embedding) VALUES (vec_f32(?));
-- Capture last_insert_rowid()
INSERT INTO chunks (rowid, file_id, chunk_index, text) VALUES (?, ?, ?, ?);
```

### Vector Search

```sql
SELECT c.text, f.path, f.wds_kind,
       distance
FROM vec_chunks
JOIN chunks c ON c.rowid = vec_chunks.rowid
JOIN ingested_files f ON f.id = c.file_id
WHERE embedding MATCH vec_f32(?)        -- query embedding
  AND k = ?                             -- TopK from config
ORDER BY distance
```

Serialise `float[]` to `byte[]` for `vec_f32()` binding: `MemoryMarshal.AsBytes(embedding.AsSpan()).ToArray()`.

---

## Ingestion Pipeline

### Staleness Check

```csharp
bool IsStale(string path, DateTime lastModified)
```
Query `ingested_files` by `path`. If no row → not ingested. If row exists and `last_modified` differs → stale. Stale files: delete old `vec_chunks` rows (by stored `vec_row_ids`), delete old `chunks` rows, re-ingest.

### Pipeline Steps

```
foreach selected file:
  1. Check staleness → skip if current
  2. IFilterExtractor.Extract(path) → string? text
     If null → log "No IFilter for {ext}, skipping" → continue
  3. Chunker.Chunk(text) → IEnumerable<string> chunks
  4. foreach chunk:
       OllamaClient.Embed(chunk) → float[] embedding
       VectorStore.InsertChunk(fileId, chunkIndex, chunk, embedding)
  5. TrackerDb.MarkIngested(path, size, lastModified, chunkCount, vecRowIds)
  6. TrackerDb.UpdateKbStats()
```

Show a Spectre.Console progress bar per file during ingestion.

---

## Retrieval

### Query Flow

```
1. OllamaClient.Embed(userQuery) → float[] queryEmbedding
2. VectorStore.Search(queryEmbedding, topK, minScore) → List<ChunkResult>
3. Group results by source file
4. Assemble context string:
   foreach file group:
     "[Source: {filename}]\n{chunk1}\n{chunk2}\n---\n"
5. Build message list:
   system: <system prompt>
   + previous conversation turns (from current session)
   + user: "Context:\n{assembledContext}\n\nQuestion: {userQuery}"
6. OllamaClient.Chat(messages) → stream response to terminal
```

### ChunkResult Model

```csharp
public record ChunkResult(
    string Text,
    string FilePath,
    string FileName,
    string? Kind,
    float Distance
);
```

---

## Spectre.Console REPL (`Repl.cs`)

### Startup Banner

```
╔══════════════════════════════════╗
║  recall — your personal memory  ║
║  KB: {N} files · {M} chunks     ║
║  Type /help for commands         ║
╚══════════════════════════════════╝
```

### Commands

| Input | Action |
|---|---|
| `/<enter>` or `/help` | Show command list |
| `/search <query>` | Discovery only — show candidate files, no ingestion |
| `/ingest <query>` | Discovery → show candidates → prompt for selection → ingest selected |
| `/kb` | Show KB stats (files, chunks, last updated) |
| `/clear` | Clear current conversation context (does not delete KB) |
| `/forget <path>` | Remove a file from KB (delete its chunks and tracker row) |
| `/exit` or `/quit` | Exit |
| Any other input | Treated as a chat query against the KB |

### Selection Prompt (After `/ingest` or inline discovery)

```
Found 6 candidates:
  [1]  Vaza_Fidelis_Proposal_v2.docx    C:\Clients\Vaza\    48 KB    2 days ago   ✓ in KB
  [2]  Vaza_Meeting_Notes.docx          C:\Clients\Vaza\    12 KB    5 days ago   ✗ not in KB
  [3]  Vaza_Holdings_Email_Thread.msg   C:\Mail\Archive\    31 KB    1 week ago   ✗ not in KB
  [4]  Fineract_Notes.txt               C:\Dev\Fidelis\      6 KB    3 days ago   ✗ not in KB

Ingest which? (1,3 / all / skip):
```

Colourize: green = already in KB, yellow = stale, white = not ingested.

### Chat Response Rendering

Stream tokens from Ollama and write directly to console using `AnsiConsole.Write`. Prefix response with a subtle `▸ recall:` label. After response, show source files used: `[Sources: Vaza_Fidelis_Proposal_v2.docx, Vaza_Holdings_Email_Thread.msg]`

### Implicit Discovery on Chat Query

When the user types a plain query (not a `/command`):

1. First search `personal_kb` for relevant chunks.
2. If results exist and `minSimilarityScore` is met → answer directly from KB. No discovery prompt.
3. If results are weak or empty → run discovery and offer ingestion before answering.
4. If KB is empty → always run discovery first and explain why.

---

## Intent Classifier (`IntentClassifier.cs`)

Simple rule-based classifier for v1. No LLM call needed:

- Starts with `/` → command, parse accordingly
- Contains a file extension (`.docx`, `.pdf`, `.msg`, etc.) → likely a file-focused query, weight discovery higher
- Contains words like "find", "search", "list", "show me files" → run discovery, not chat
- Everything else → chat query against KB

Add an `IsAmbiguous` flag so the REPL can ask: `"Search for files or query your knowledge base? (s/k)"` when unclear.

---

## install.ps1

```powershell
# recall installer bootstrap
# Run as: .\install.ps1

$ErrorActionPreference = "Stop"

Write-Host "recall installer" -ForegroundColor Cyan
Write-Host "================" -ForegroundColor Cyan

# 1. Check Ollama
if (-not (Get-Command ollama -ErrorAction SilentlyContinue)) {
    Write-Host "`nOllama not found. Downloading installer..." -ForegroundColor Yellow
    $ollamaInstaller = "$env:TEMP\OllamaSetup.exe"
    Invoke-WebRequest -Uri "https://ollama.com/download/OllamaSetup.exe" -OutFile $ollamaInstaller
    Start-Process $ollamaInstaller -Wait
    Write-Host "Ollama installed." -ForegroundColor Green
} else {
    Write-Host "Ollama found." -ForegroundColor Green
}

# 2. Pull models
Write-Host "`nPulling nomic-embed-text (embedding model)..." -ForegroundColor Cyan
ollama pull nomic-embed-text

Write-Host "`nPulling qwen3:8b (chat model, ~5.2 GB, unattended)..." -ForegroundColor Cyan
ollama pull qwen3:8b

# 3. Verify libs
$libs = @("libs\Everything64.dll", "libs\vec0.dll")
foreach ($lib in $libs) {
    if (-not (Test-Path $lib)) {
        Write-Host "MISSING: $lib — place in the libs\ folder before running recall." -ForegroundColor Red
    } else {
        Write-Host "Found: $lib" -ForegroundColor Green
    }
}

# 4. Create AppData directory
$dataDir = "$env:APPDATA\Recall"
if (-not (Test-Path $dataDir)) {
    New-Item -ItemType Directory -Path $dataDir | Out-Null
}

Write-Host "`nInstallation complete. Run recall.exe to start." -ForegroundColor Green
```

---

## README.md Content

Include a README covering:

1. Prerequisites (Windows 10/11 x64, .NET 8 runtime if not self-contained)
2. Where to get `Everything64.dll` — direct link: `https://www.voidtools.com/downloads/` → "Download Everything SDK"
3. Where to get `vec0.dll` — from sqlite-vec GitHub releases: `https://github.com/asg017/sqlite-vec/releases`
4. Running `install.ps1` — one-time setup
5. First run walkthrough
6. Command reference table
7. Privacy note: all data stays on this machine, no cloud calls except Ollama model downloads

---

## Build & Publish Command

```
dotnet publish Recall.Cli/Recall.Cli.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=false \
  -o ./publish
```

Copy `libs/` folder and `appsettings.json` into `./publish` as post-build step. Configure this in `Recall.Cli.csproj` using `<Target Name="CopyNativeLibs" AfterTargets="Publish">`.

---

## Implementation Order

Build in this sequence to keep things testable at each step:

1. `Recall.Storage` — schema, TrackerDb, sqlite-vec loading and insert/search
2. `Recall.Ollama` — health check, embed, chat (streaming)
3. `Recall.Discovery` — Everything P/Invoke, WDS OLE DB, merge logic
4. `Recall.Ingestion` — IFilter extractor, chunker, pipeline orchestrator
5. `Recall.Retrieval` — vector search, context assembly
6. `Recall.Cli` — REPL, intent classifier, wire everything together
7. `install.ps1` and `README.md`

Test each layer independently before wiring into the REPL. The REPL is last.

---

## Constraints

- No file parsers beyond IFilter (no PdfPig, Open XML, iTextSharp, etc.)
- No cloud API calls during operation (Ollama is local)
- No Windows Service or background process — the app is the process
- No GUI — CLI only for this version
- Target Windows 10 x64 minimum
- `net8.0-windows` TFM required for `System.Data.OleDb` and COM interop
- Handle IFilter failures gracefully — never surface a raw COM exception to the user
- All paths must handle spaces correctly (quoted or verbatim string literals)
- `recall.db` must auto-create on first run with no manual setup
