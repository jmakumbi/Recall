# Recall — Completed Phases

Phases are released as GitHub tags once all tasks in the phase are done.

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
