# Recall — Completed Phases

Phases are released as GitHub tags once all tasks in the phase are done.

---

## Phase 6 — `Recall.Cli` — v0.6.0 — 2026-05-23

**Released:** [v0.6.0](https://github.com/jmakumbi/Recall/releases/tag/v0.6.0)

Implemented the Spectre.Console REPL and wired all six layers together:

- `appsettings.json` — runtime configuration for all layers (DB path, vec0/Everything DLL paths, search paths, Ollama config, ingestion + retrieval tuning)
- `AppConfig.cs` — runtime config model + `SaveAsync()` that surgically updates only `Recall.SearchPaths` in appsettings.json using `System.Text.Json.Nodes`, preserving all other settings
- `IntentClassifier.cs` — classifies free-text input as `Command` (/ prefix), `Discovery` (find/search keywords or file extensions), `Chat` (what/why/how keywords), or `Ambiguous` (Spectre.Console SelectionPrompt to disambiguate)
- `Repl.cs` — full interactive REPL:
  - FigletText banner with KB stats panel on startup; nudge to `/setup` when no paths configured
  - `/setup` — interactive add/remove wizard, env-var expansion, validates path exists, writes back to appsettings.json, calls `DiscoveryService.UpdateSearchPaths()` to reload at runtime
  - `/search` + `/ingest` — discovery with colourized result table (green=in KB, yellow=stale, dim=not ingested); prompts to ingest new/stale files
  - Chat — `Retriever.QueryAsync` → `AssembleContext` → `OllamaClient.ChatAsync` streaming; `▸ recall:` prefix; `[Sources: ...]` footer; 10-exchange rolling history
  - All commands: `/help`, `/kb`, `/clear`, `/forget`, `/exit`/`/quit`
- `Program.cs` — `IConfiguration` loading from appsettings.json, DI container wiring of all six layers, Ctrl+C cancellation, startup health check with per-model warnings
- `Recall.Cli.csproj` — assembly name `recall`; `CopyNativeLibs` post-publish target copies `libs/` next to exe

**Notes:**
- `DiscoveryService` made partially mutable (non-readonly fields) to support runtime path reload via `UpdateSearchPaths()`

---

## Phase 5 — `Recall.Retrieval` — v0.5.0 — 2026-05-23

**Released:** [v0.5.0](https://github.com/jmakumbi/Recall/releases/tag/v0.5.0)

Implemented the retrieval and context-assembly layer:

- `RetrievalModels.cs` — `ChunkResult` record (`RowId`, `FileId`, `ChunkIndex`, `Text`, `FilePath`, `FileName`, `Kind`, `Distance`); `RetrievalConfig` (`TopK=5`, `MaxDistance=1.0`, `MaxContextChars=6000`)
- `Retriever.cs`
  - `QueryAsync()` — embeds the user query via `OllamaClient.EmbedAsync`, searches `VectorStore`, maps rows to `ChunkResult`
  - `AssembleContext()` — groups chunks by source file, formats as `[Source: filename]\ntext\n---`, caps at `MaxContextChars` with partial-chunk truncation
- `Recall.RetrievalTest` — seeds a 2-file KB (Recall topic vs. solar system), queries for Recall content, validates top result is from the correct file

**Verified:** 35 chunks across 2 files · top-5 query hits ✓ · closest chunk from correct topic file (L2≈0.83) ✓ · `[Source:]` headers in assembled context ✓ · 500-char cap respected (499 chars) ✓

---

## Phase 4 — `Recall.Ingestion` — v0.4.0 — 2026-05-23

**Released:** [v0.4.0](https://github.com/jmakumbi/Recall/releases/tag/v0.4.0)

Implemented the text extraction, chunking, and ingestion pipeline layer:

- `IngestionConfig.cs` — `ChunkSize` (512 tokens), `ChunkOverlap` (100), `MaxExtractedCharsPerFile` (500 000 chars); `IngestionProgress` progress record
- `IFilterExtractor.cs` — Windows IFilter COM interop; `[ComImport]` `IFilter` interface (`{89BCB740-6119-101A-BCB7-00DD010655AF}`); `LoadIFilter` P/Invoke from `query.dll`; extraction loop `GetChunk` → `GetText` → accumulate; dedicated STA thread with 15s timeout; graceful null return on all failures; `virtual Extract()` to allow test overrides
- `Chunker.cs` — sliding-window word-boundary chunker; token heuristic `chars / 4`; overlap by walking back `overlapChars` words; discards sub-50-token tail chunks
- `IngestionPipeline.cs` — full extract → chunk → embed → store flow; staleness check via `LastWriteTimeUtc`; deletes old vec rows before re-ingesting; double `MarkIngested` to capture vec row IDs; `IProgress<IngestionProgress>` support
- `Recall.IngestionTest` — chunker unit test ✓ · IFilterExtractor with real document ✓ · full pipeline with `PlainTextExtractor` stub ✓
- `OllamaClient.Normalize()` — L2-normalise embeddings to unit vectors so L2 distance ∈ [0, 2] (avoids unbounded raw distances from nomic-embed-text); applied in `EmbedAsync` for both storage and search

**Verified:** 27 chunks embedded and stored · ANN search returning 3 results (closest L2 ≈ 0.81) · re-ingest correctly skipped ✓

**Notes:**
- IFilter `LoadIFilter` times out on OneDrive-backed files on dev machine — graceful 15s timeout handles it; local files work correctly
- Embedding normalisation is critical: raw nomic-embed-text vectors have unbounded L2 distances (>> 2.0); unit-normalised vectors cap max distance at 2.0 (opposite directions)

---

## Phase 3 — `Recall.Discovery` — v0.3.0 — 2026-05-23

**Released:** [v0.3.0](https://github.com/jmakumbi/Recall/releases/tag/v0.3.0)

Implemented the file discovery layer:

- `DiscoveryResult.cs` — record with `FullPath`, `FileName`, `Extension`, `SizeBytes`, `LastModified`, `WdsSnippet`, `WdsKind`, `AlreadyIngested`, `IsStale`; `DiscoveryConfig`
- `EverythingClient.cs` — P/Invoke via `NativeLibrary.SetDllImportResolver`; searches scoped to `DefaultSearchScope`; one-time IPC warning
- `WindowsSearchClient.cs` — WDS OLE DB `Search.CollatorDSO.1`; inline-SQL workaround (provider doesn't implement `ICommandWithParameters`); single-quote escaping; graceful fallback on OLE DB failure
- `DiscoveryService.cs` — orchestrates both backends, merges (Everything canonical, WDS enriches), deduplicates by normalised path, enriches `AlreadyIngested`/`IsStale` from `TrackerDb`
- `Recall.DiscoveryTest` — smoke test; WDS returning live document results ✓

**Notes:**
- Everything not installed on dev machine — graceful degradation confirmed ✓
- WDS inline-SQL fix required: `Search.CollatorDSO.1` rejects `ICommandWithParameters`

---

## Phase 2 — `Recall.Ollama` — v0.2.0 — 2026-05-23

**Released:** [v0.2.0](https://github.com/jmakumbi/Recall/releases/tag/v0.2.0)

Implemented the Ollama HTTP client layer:

- `OllamaModels.cs` — `OllamaConfig`, `ChatMessage` (system/user/assistant factory methods), `OllamaHealthResult`, internal request/response DTOs
- `OllamaUnavailableException` — thrown on connection refused; REPL will catch and show friendly message
- `OllamaClient.cs`
  - `HealthCheckAsync()` — `GET /api/tags`, fuzzy model name matching (handles `:latest` suffix)
  - `EmbedAsync()` — `POST /api/embeddings`, returns `float[]`; validates non-empty result
  - `ChatAsync()` — streaming `POST /api/chat` via `HttpCompletionOption.ResponseHeadersRead`, yields tokens as `IAsyncEnumerable<string>`
  - `SystemPrompt` constant — knowledge-assistant persona
- `Recall.OllamaTest` — smoke test with live Ollama; falls back to any available model for streaming path verification

**Verified:** health check ✓ · embed (768-dim) ✓ · streaming chat ✓

---

## Phase 1 — `Recall.Storage` — v0.1.0 — 2026-05-23

**Released:** [v0.1.0](https://github.com/jmakumbi/Recall/releases/tag/v0.1.0) · [v0.1.1](https://github.com/jmakumbi/Recall/releases/tag/v0.1.1) (patch)

Implemented the full persistence foundation:

- `Schema.cs` — DDL for all five tables (`ingested_files`, `vec_chunks`, `chunks`, `conversations`, `kb_stats`)
- `Models.cs` — `TrackedFile`, `KbStats`, `ChunkRow` records
- `TrackerDb.cs` — file ingestion tracker with staleness detection, vec row ID tracking, KB stats
- `VectorStore.cs` — sqlite-vec insert, delete, and ANN search via `vec_f32()`
- `Recall.StorageTest` — smoke test runner (skips gracefully without `libs\vec0.dll`)

**Patch v0.1.1:** `VectorStore.Search` — replaced direct JOIN on `vec_chunks` with a CTE to correctly capture the `distance` column (returns NULL when accessed via JOIN in sqlite-vec).

**Notes:**
- `net8.0-windows` TFM throughout
- `vec0.dll` loaded at runtime via `connection.LoadExtension()` — not committed, user-supplied
- `recall.db` auto-creates in any path via `Directory.CreateDirectory` before `SqliteConnection.Open()`
- Both `Everything64.dll` (from Everything SDK zip) and `vec0.dll` (from `sqlite-vec-0.1.9-loadable-windows-x86_64.tar.gz`) verified and smoke-tested
