# Recall — TODO

Tasks are organized by implementation phase. Complete each phase before moving to the next.
Phases map directly to the build order in the spec.

---

## Phase 1 — `Recall.Storage`

- [ ] Create solution `Recall.sln` and project `Recall.Storage/Recall.Storage.csproj`
- [ ] Add `Microsoft.Data.Sqlite` NuGet reference
- [ ] Implement `Schema.cs` — DDL for `ingested_files`, `vec_chunks`, `chunks`, `conversations`, `kb_stats`
- [ ] Implement `TrackerDb.cs`
  - [ ] `OpenAndInit(string dbPath)` — opens SQLite, loads vec0 extension, runs schema DDL
  - [ ] `IsIngested(string path) → bool`
  - [ ] `GetTrackedFile(string path) → TrackedFile?`
  - [ ] `MarkIngested(string path, long size, DateTime lastModified, int chunkCount, long[] vecRowIds)`
  - [ ] `DeleteFile(string path)` — removes tracker row + chunk rows + vec rows
  - [ ] `GetKbStats() → KbStats`
  - [ ] `UpdateKbStats()`
- [ ] Implement `VectorStore.cs` (in Recall.Storage or new Recall.Retrieval stub)
  - [ ] `InsertChunk(int fileId, int chunkIndex, string text, float[] embedding) → long rowId`
  - [ ] `DeleteChunks(long[] rowIds)`
  - [ ] `Search(float[] queryEmbedding, int topK, float minScore) → List<ChunkRow>`
- [ ] Load `vec0.dll` via `connection.LoadExtension()` with absolute path resolution
- [ ] Verify `recall.db` auto-creates on first run with no manual setup
- [ ] Test storage layer in isolation (manual run or xUnit project)

---

## Phase 2 — `Recall.Ollama`

- [ ] Create project `Recall.Ollama/Recall.Ollama.csproj`
- [ ] Add `System.Text.Json` reference
- [ ] Implement `OllamaModels.cs` — request/response DTOs for embed and chat
- [ ] Implement `OllamaClient.cs`
  - [ ] `HealthCheck() → OllamaHealthResult` — `GET /api/tags`, verify both models present
  - [ ] `Embed(string text) → float[]` — `POST /api/embeddings`
  - [ ] `Chat(IEnumerable<ChatMessage> messages) → IAsyncEnumerable<string>` — streaming `POST /api/chat`
- [ ] Define `OllamaUnavailableException` — thrown on connection refused
- [ ] Surface startup warning if `nomic-embed-text` or `qwen3:8b` is missing
- [ ] Test embed and chat against a running Ollama instance

---

## Phase 3 — `Recall.Discovery`

- [ ] Create project `Recall.Discovery/Recall.Discovery.csproj`
- [ ] Add `System.Data.OleDb` NuGet reference
- [ ] Define `DiscoveryResult` record with all fields from spec
- [ ] Implement `EverythingClient.cs`
  - [ ] P/Invoke declarations for all `Everything_*` functions
  - [ ] `NativeLibrary.Load()` with configured `EverythingDllPath`
  - [ ] `Search(string query, uint maxResults) → IEnumerable<DiscoveryResult>`
  - [ ] One-time warning if Everything service not detected
- [ ] Implement `WindowsSearchClient.cs`
  - [ ] OLE DB connection with `Provider=Search.CollatorDSO.1`
  - [ ] Parameterised query template from spec
  - [ ] `Search(string query, string scope) → IEnumerable<DiscoveryResult>`
- [ ] Merge Everything + WDS results: Everything canonical, WDS enriches `AutoSummary`/`Kind`
- [ ] Normalise `FullPath` (lowercase, normalised separators) for dedup
- [ ] Populate `AlreadyIngested` and `IsStale` via `TrackerDb` lookup after merge
- [ ] Test discovery layer with real Windows Search and a test query

---

## Phase 4 — `Recall.Ingestion`

- [ ] Create project `Recall.Ingestion/Recall.Ingestion.csproj`
- [ ] Implement `IFilterExtractor.cs`
  - [ ] `[ComImport]` declarations for `IFilter` and `IFilterChunk` with correct GUIDs
  - [ ] P/Invoke `LoadIFilter` from `query.dll`
  - [ ] Extraction loop: `GetChunk` → `GetText` → accumulate, stop at `MaxExtractedCharsPerFile`
  - [ ] Graceful handling of `FILTER_E_NO_FILTER_FOR_EXT`, `FILTER_E_ACCESS`, and any HRESULT
  - [ ] COM release in `finally` blocks
- [ ] Implement `Chunker.cs`
  - [ ] `Chunk(string text, int chunkSize, int overlap) → IEnumerable<string>`
  - [ ] Word-boundary splitting, token heuristic `text.Length / 4`
  - [ ] Overlap = last N tokens prepended to next chunk
  - [ ] Discard chunks shorter than 50 tokens
- [ ] Implement `IngestionPipeline.cs`
  - [ ] Staleness check per file
  - [ ] Orchestrate: extract → chunk → embed → store
  - [ ] Delete stale chunks before re-ingesting
  - [ ] Update `kb_stats` after each file
  - [ ] Spectre.Console progress bar per file (or stub for now, wired in CLI phase)
- [ ] Test ingestion end-to-end on a small set of `.docx`, `.pdf`, `.txt` files

---

## Phase 5 — `Recall.Retrieval`

- [ ] Create project `Recall.Retrieval/Recall.Retrieval.csproj`
- [ ] Define `ChunkResult` record: `Text`, `FilePath`, `FileName`, `Kind`, `Distance`
- [ ] Implement `Retriever.cs`
  - [ ] `Query(string userQuery) → List<ChunkResult>`
    - [ ] Embed query via `OllamaClient.Embed()`
    - [ ] Search via `VectorStore.Search()`
    - [ ] Filter by `MinSimilarityScore`
  - [ ] `AssembleContext(List<ChunkResult> chunks) → string`
    - [ ] Group by source file
    - [ ] Format: `[Source: {filename}]\n{chunks}\n---\n`
- [ ] Test retrieval with queries against an already-ingested KB

---

## Phase 6 — `Recall.Cli`

- [ ] Create project `Recall.Cli/Recall.Cli.csproj` (console app)
- [ ] Add `Spectre.Console`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Configuration.Json`
- [ ] Implement `Program.cs` — DI setup, config loading, startup health check
  - [ ] Expand `%APPDATA%` / `%USERPROFILE%` in config paths at startup
  - [ ] Load `appsettings.json` from exe directory
- [ ] Implement `IntentClassifier.cs`
  - [ ] `/` prefix → command
  - [ ] File extension hints → discovery-weighted
  - [ ] Find/search/list keywords → discovery
  - [ ] `IsAmbiguous` flag → prompt `"Search for files or query your knowledge base? (s/k)"`
  - [ ] Default → chat query
- [ ] Implement `Repl.cs` — main REPL loop
  - [ ] Startup banner with KB stats
  - [ ] `/help` — command list
  - [ ] `/search <query>` — discovery only, no ingestion
  - [ ] `/ingest <query>` — discovery → selection prompt → ingest
  - [ ] `/kb` — KB stats display
  - [ ] `/clear` — clear conversation context
  - [ ] `/forget <path>` — remove file from KB
  - [ ] `/exit` / `/quit` — exit
  - [ ] Plain query → implicit discovery/chat flow (spec §"Implicit Discovery on Chat Query")
  - [ ] Colourize candidate list: green=in KB, yellow=stale, white=not ingested
  - [ ] Stream chat tokens via `AnsiConsole.Write`, prefix `▸ recall:`
  - [ ] Show `[Sources: ...]` after each response
- [ ] Wire all six layers together via DI
- [ ] Configure `Recall.Cli.csproj` post-publish `CopyNativeLibs` target

---

## Phase 7 — Packaging & Distribution

- [ ] Create `install.ps1` — Ollama install, model pull, lib verification, AppData dir
- [ ] Finalize `appsettings.json` defaults (matches spec exactly)
- [ ] Configure self-contained publish: `net8.0-windows`, `win-x64`, `PublishSingleFile=false`
- [ ] Verify `libs/Everything64.dll` and `libs/vec0.dll` are copied on publish
- [ ] Smoke test published binary on a clean machine (no SDK installed)
- [ ] Create GitHub release with published binary zip + `install.ps1`

---

## Backlog / Stretch Goals

- [ ] `/export` command — dump KB to JSON
- [ ] `/stats` — per-file ingestion stats
- [ ] Support `mbox` email archives via IFilter (if IFilter available)
- [ ] `--headless` mode for scripting without REPL
- [ ] Configurable chat model per session
