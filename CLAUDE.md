# CLAUDE.md — Recall

## What this project is

`recall` is a Windows-only CLI that lets users chat with their personal files via local LLMs (Ollama). It combines file discovery (Everything SDK + Windows Search), text extraction (IFilter COM), vector storage (sqlite-vec), and a streaming chat UI (Spectre.Console).

There are **no cloud API calls during operation**. Everything runs locally.

## Solution layout

```
Recall/
├── Recall.sln
├── Recall.Cli/          ← Console app, Spectre.Console REPL (Phase 6)
├── Recall.Discovery/    ← Everything P/Invoke + WDS OLE DB (Phase 3)
├── Recall.Ingestion/    ← IFilter COM + Chunker + Pipeline (Phase 4)
├── Recall.Retrieval/    ← Vector search + context assembly (Phase 5)
├── Recall.Ollama/       ← Ollama HTTP client, embed + chat (Phase 2)
├── Recall.Storage/      ← SQLite tracker + sqlite-vec init (Phase 1)
├── libs/                ← Everything64.dll + vec0.dll (not NuGet, not committed)
├── install.ps1          ← Bootstrap installer
├── appsettings.json     ← Runtime config, lives alongside recall.exe
├── TODO.md              ← Phase-by-phase task list
├── COMPLETED.md         ← Released phases log
└── README.md            ← User-facing docs
```

## Build order

Always build and test phases in sequence — each layer depends on the previous:

1. `Recall.Storage` — DB schema, sqlite-vec extension loading, TrackerDb, VectorStore
2. `Recall.Ollama` — Ollama HTTP client (embed + streaming chat)
3. `Recall.Discovery` — Everything P/Invoke + WDS OLE DB merge
4. `Recall.Ingestion` — IFilter COM extraction, Chunker, IngestionPipeline
5. `Recall.Retrieval` — Retriever (query embed → ANN search → context assembly)
6. `Recall.Cli` — REPL, IntentClassifier, DI wiring, post-publish copy target
7. Packaging — install.ps1, publish, GitHub release

## Key implementation constraints

- **Target framework:** `net8.0-windows` (mandatory for `System.Data.OleDb` and COM interop)
- **No file parsers beyond IFilter** — no PdfPig, Open XML SDK, iTextSharp, etc.
- **No cloud calls during operation** — Ollama is local; only model downloads hit the network
- **No GUI** — CLI-only; Spectre.Console for rich terminal output
- **Self-contained publish** — `win-x64`, `PublishSingleFile=false`
- **`recall.db` auto-creates** on first run; no user setup required beyond `install.ps1`
- **All paths must handle spaces** — use verbatim string literals or explicit quoting
- **Never surface raw COM exceptions** to the user; always log and return null from IFilter failures

## Native DLLs

`libs/` is `.gitignore`d. Users must supply:

- `Everything64.dll` — from <https://www.voidtools.com/downloads/> (Everything SDK)
- `vec0.dll` — from <https://github.com/asg017/sqlite-vec/releases>

Load both at runtime:
- `Everything64.dll` via `NativeLibrary.Load(absolutePath)` in `EverythingClient`
- `vec0.dll` via `connection.LoadExtension("libs\\vec0")` (no `.dll` suffix on Windows) in `TrackerDb.OpenAndInit`

## Config

`appsettings.json` lives alongside the executable. Key paths use `%APPDATA%` and `%USERPROFILE%` — always expand with `Environment.ExpandEnvironmentVariables()` at startup, never inline.

## Ollama

- Embedding: `POST /api/embeddings` → `float[]` (768-dim for nomic-embed-text)
- Chat: `POST /api/chat` with `"stream": true` → yield tokens as `IAsyncEnumerable<string>`
- Startup health check: `GET /api/tags` — warn if either model is missing with the pull command
- On connection refused: throw `OllamaUnavailableException` (custom); REPL catches and shows friendly message

## sqlite-vec

- Virtual table: `vec_chunks USING vec0(embedding float[768])`
- Insert: `INSERT INTO vec_chunks (embedding) VALUES (vec_f32(?))` then capture `last_insert_rowid()`
- Search: `WHERE embedding MATCH vec_f32(?) AND k = ?`
- Serialise `float[]` → `byte[]`: `MemoryMarshal.AsBytes(embedding.AsSpan()).ToArray()`

## IFilter COM

- `LoadIFilter` P/Invoked from `query.dll` — not in managed code, declare inline
- `[ComImport]` `IFilter` with GUID `{89BCB740-6119-101A-BCB7-00DD010655AF}`
- Loop: `GetChunk()` → check `CHUNKSTATE` for text → `GetText()` until `FILTER_E_NO_MORE_TEXT`
- Stop at `MaxExtractedCharsPerFile` (default 500 000 chars)
- Always `Marshal.ReleaseComObject` in `finally`; never throw to caller

## Release process

When a phase is fully done:
1. Move its tasks to `COMPLETED.md` with a date
2. Commit all changes
3. `git push`
4. `gh release create vN.N.0 --title "Phase N — <Name>" --notes "..."`

Current phase tags: none yet.
