using Recall.Ollama;
using Recall.Storage;

namespace Recall.Ingestion;

/// <summary>
/// Orchestrates the full ingestion flow for a set of files:
/// extract → chunk → embed → store, with staleness checking and progress reporting.
/// </summary>
public sealed class IngestionPipeline
{
    private readonly IFilterExtractor _extractor;
    private readonly TrackerDb        _tracker;
    private readonly VectorStore      _vectors;
    private readonly OllamaClient     _ollama;
    private readonly IngestionConfig  _config;

    public IngestionPipeline(
        IngestionConfig config,
        IFilterExtractor extractor,
        TrackerDb tracker,
        VectorStore vectors,
        OllamaClient ollama)
    {
        _config    = config;
        _extractor = extractor;
        _tracker   = tracker;
        _vectors   = vectors;
        _ollama    = ollama;
    }

    /// <summary>
    /// Ingest each file in <paramref name="filePaths"/>.
    /// Skips files that are already current in the KB.
    /// Re-ingests stale files (deletes old chunks first).
    /// Automatically retries files that were partially written by a previous
    /// interrupted run (chunk_count == 0 in the tracker).
    /// Reports progress via <paramref name="progress"/> if provided.
    /// </summary>
    /// <remarks>
    /// Each file is written atomically: all embeddings are collected in memory
    /// first, then committed to the DB in a single SQLite transaction.
    /// Interruption during embedding leaves the DB untouched; interruption
    /// mid-transaction is rolled back by SQLite automatically.
    /// </remarks>
    public async Task IngestAsync(
        IEnumerable<string> filePaths,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken ct = default)
    {
        var paths = filePaths.ToList();
        int total = paths.Count;

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();

            var path     = paths[i];
            var fileName = Path.GetFileName(path);

            void Report(string phase, int chunks = 0) =>
                progress?.Report(new IngestionProgress(path, fileName, i + 1, total, chunks, phase));

            // ── Staleness / interrupt-recovery check ──────────────────────
            FileInfo fi;
            try   { fi = new FileInfo(path); }
            catch { Report("error"); continue; }

            if (!fi.Exists) { Report("skipped"); continue; }

            var tracked = _tracker.GetTrackedFile(path);
            if (tracked is not null)
            {
                bool isStale      = fi.LastWriteTimeUtc > tracked.LastModified.ToUniversalTime();
                bool isIncomplete = tracked.ChunkCount == 0 && tracked.VecRowIds.Length == 0;
                if (!isStale && !isIncomplete) { Report("skipped"); continue; }
                // isIncomplete means a prior run was interrupted — clean up and redo
            }

            // ── Extract ───────────────────────────────────────────────────
            Report("extracting");
            var text = _extractor.Extract(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                // No extractor for this type — track with 0 chunks so we
                // don't retry it on every search.
                _tracker.MarkIngested(path, fi.Length, fi.LastWriteTimeUtc, 0, []);
                Report("skipped");
                continue;
            }

            // ── Chunk ─────────────────────────────────────────────────────
            Report("chunking");
            var chunks = Chunker.Chunk(text, _config.ChunkSize, _config.ChunkOverlap).ToList();
            if (chunks.Count == 0) { Report("skipped"); continue; }

            // ── Phase 1: Embed all chunks (no DB writes) ──────────────────
            // Collect all embeddings in memory before touching the database.
            // If this is cancelled or fails, the DB is left completely unchanged.
            var embeddings = new List<float[]>(chunks.Count);
            for (int c = 0; c < chunks.Count; c++)
            {
                ct.ThrowIfCancellationRequested();
                Report("embedding", c + 1);
                embeddings.Add(await _ollama.EmbedAsync(chunks[c], ct));
            }

            // ── Phase 2: Atomic commit ────────────────────────────────────
            // All DB writes happen inside one transaction.
            // If anything fails here, SQLite rolls back the entire file.
            using var tx = _tracker.BeginTransaction();
            try
            {
                // Remove any stale or orphaned chunks from a prior run
                if (tracked is not null && tracked.VecRowIds.Length > 0)
                    _vectors.DeleteChunks(tracked.VecRowIds, tx);

                // Write the file record (final chunk count), get file_id
                var record = _tracker.MarkIngested(
                    path, fi.Length, fi.LastWriteTimeUtc, chunks.Count, [], tx);

                // Write all chunks + embeddings
                var rowIds = new List<long>(chunks.Count);
                for (int c = 0; c < chunks.Count; c++)
                {
                    Report("storing", c + 1);
                    var rowId = _vectors.InsertChunk(record.Id, c, chunks[c], embeddings[c], tx);
                    rowIds.Add(rowId);
                }

                // Update vec_row_ids now that we have them all
                _tracker.UpdateVecRowIds(record.Id, [.. rowIds], tx);

                tx.Commit();
                _tracker.UpdateKbStats();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            Report("done", chunks.Count);
        }
    }
}
