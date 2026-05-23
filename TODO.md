# Recall — TODO

Tasks are organized by implementation phase. Complete each phase before moving to the next.
Phases map directly to the build order in the spec.

---

## Phase 1 — `Recall.Storage` ✅ DONE (v0.1.0)

- [x] Create solution `Recall.sln` and project `Recall.Storage/Recall.Storage.csproj`
- [x] Add `Microsoft.Data.Sqlite` NuGet reference
- [x] Implement `Schema.cs` — DDL for `ingested_files`, `vec_chunks`, `chunks`, `conversations`, `kb_stats`
- [x] Implement `TrackerDb.cs`
  - [x] `OpenAndInit(string dbPath)` — opens SQLite, loads vec0 extension, runs schema DDL
  - [x] `IsIngested(string path) → bool`
  - [x] `GetTrackedFile(string path) → TrackedFile?`
  - [x] `MarkIngested(string path, long size, DateTime lastModified, int chunkCount, long[] vecRowIds)`
  - [x] `DeleteFile(string path)` — removes tracker row + chunk rows + vec rows
  - [x] `GetKbStats() → KbStats`
  - [x] `UpdateKbStats()`
- [x] Implement `VectorStore.cs`
  - [x] `InsertChunk(int fileId, int chunkIndex, string text, float[] embedding) → long rowId`
  - [x] `DeleteChunks(long[] rowIds)`
  - [x] `Search(float[] queryEmbedding, int topK, float minScore) → List<ChunkRow>`
- [x] Load `vec0.dll` via `connection.LoadExtension()` with absolute path resolution
- [x] `recall.db` auto-creates on first run (Directory.CreateDirectory + SQLite auto-create)
- [x] Smoke test runner (`Recall.StorageTest`) — skips gracefully when `libs\vec0.dll` absent

---

## Phase 2 — `Recall.Ollama` ✅ DONE (v0.2.0)

- [x] Create project `Recall.Ollama/Recall.Ollama.csproj`
- [x] `System.Text.Json` used (BCL, no extra NuGet needed)
- [x] Implement `OllamaModels.cs` — `OllamaConfig`, `ChatMessage`, `OllamaHealthResult`, internal DTOs
- [x] Implement `OllamaClient.cs`
  - [x] `HealthCheckAsync()` — `GET /api/tags`, model name fuzzy match (handles `:latest` tags)
  - [x] `EmbedAsync(string text) → float[]` — `POST /api/embeddings`
  - [x] `ChatAsync(messages) → IAsyncEnumerable<string>` — streaming `POST /api/chat` via `ResponseHeadersRead`
- [x] Define `OllamaUnavailableException` — thrown on connection refused / socket error
- [x] Startup warning surfaced for each missing model with pull command
- [x] `Recall.OllamaTest` — smoke test: health check ✓, embed 768-dim ✓, streaming chat ✓

---

## Phase 3 — `Recall.Discovery` ✅ DONE (v0.3.0)

- [x] Create project `Recall.Discovery/Recall.Discovery.csproj`
- [x] Add `System.Data.OleDb` NuGet reference
- [x] Define `DiscoveryResult` record + `DiscoveryConfig`
- [x] Implement `EverythingClient.cs`
  - [x] P/Invoke declarations for all `Everything_*` functions
  - [x] `NativeLibrary.SetDllImportResolver` — load from configured path at runtime
  - [x] `Search(string query) → IReadOnlyList<DiscoveryResult>`
  - [x] One-time warning if Everything IPC unavailable
- [x] Implement `WindowsSearchClient.cs`
  - [x] OLE DB connection with `Provider=Search.CollatorDSO.1`
  - [x] Inline-SQL (Search.CollatorDSO.1 does not support ICommandWithParameters)
  - [x] Single-quote escaping for safe inline values
  - [x] `Search(string query) → IReadOnlyList<DiscoveryResult>`
- [x] Implement `DiscoveryService.cs` — orchestrates merge + enrichment
  - [x] Everything canonical; WDS enriches `WdsSnippet`/`WdsKind` where paths match
  - [x] Normalise + deduplicate by lowercased full path
  - [x] Populate `AlreadyIngested` and `IsStale` via optional `TrackerDb`
- [x] `Recall.DiscoveryTest` — smoke test: WDS live results ✓, merge ✓, KB status ✓

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
