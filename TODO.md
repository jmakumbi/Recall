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

## Phase 4 — `Recall.Ingestion` ✅ DONE (v0.4.0)

- [x] Create project `Recall.Ingestion/Recall.Ingestion.csproj`
- [x] Implement `IFilterExtractor.cs`
  - [x] `[ComImport]` declarations for `IFilter` with correct GUIDs
  - [x] P/Invoke `LoadIFilter` from `query.dll`
  - [x] Extraction loop: `GetChunk` → `GetText` → accumulate, stop at `MaxExtractedCharsPerFile`
  - [x] Graceful handling of all HRESULTs; returns null on any failure
  - [x] COM release in `finally` blocks; dedicated STA thread with 15s timeout
- [x] Implement `Chunker.cs`
  - [x] `Chunk(string text, int chunkSize, int overlap) → IEnumerable<string>`
  - [x] Word-boundary splitting, token heuristic `text.Length / 4`
  - [x] Overlap = last N tokens prepended to next chunk
  - [x] Discard chunks shorter than 50 tokens
- [x] Implement `IngestionPipeline.cs`
  - [x] Staleness check per file via `LastWriteTimeUtc`
  - [x] Orchestrate: extract → chunk → embed → store
  - [x] Delete stale chunks before re-ingesting
  - [x] `IProgress<IngestionProgress>` for progress reporting
- [x] Normalize embeddings in `OllamaClient.EmbedAsync` (L2 unit vectors for correct ANN distances)
- [x] `Recall.IngestionTest` — smoke test: chunker ✓ · pipeline 27 chunks ✓ · ANN search ✓ (L2 ≈ 0.81) · re-ingest skip ✓

---

## Phase 5 — `Recall.Retrieval` ✅ DONE (v0.5.0)

- [x] Create project `Recall.Retrieval/Recall.Retrieval.csproj`
- [x] Define `ChunkResult` record: `RowId`, `FileId`, `ChunkIndex`, `Text`, `FilePath`, `FileName`, `Kind`, `Distance`
- [x] Define `RetrievalConfig`: `TopK` (5), `MaxDistance` (1.0 ≈ cosine ≥ 0.5), `MaxContextChars` (6 000)
- [x] Implement `Retriever.cs`
  - [x] `QueryAsync(string userQuery) → List<ChunkResult>` — embed + vector search
  - [x] `AssembleContext(List<ChunkResult>) → string` — group by file, `[Source:]` headers, char cap
- [x] `Recall.RetrievalTest` — KB seeded ✓ · query 5 hits ✓ · top result correct file ✓ · context headers ✓ · char cap ✓

---

## Phase 6 — `Recall.Cli`

> **`/setup` path wizard** (decided during Phase 3):
> First-run interactive command that populates `SearchPaths` in `appsettings.json`.
> If `SearchPaths` is empty on startup, REPL shows: `"No search paths configured — run /setup to get started."`

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
  - [ ] `/setup` — interactive path wizard
  - [ ] If `SearchPaths` is empty on startup, show nudge: `"No search paths configured — run /setup"`
  - [ ] List current paths, prompt `[1] Add folder  [2] Remove folder  [3] Done`
  - [ ] Validate each path exists; expand env vars before saving
  - [ ] Write updated `SearchPaths` array back to `appsettings.json`
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
