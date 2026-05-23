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
    /// Reports progress via <paramref name="progress"/> if provided.
    /// </summary>
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

            // ── Staleness check ───────────────────────────────────────────
            FileInfo fi;
            try   { fi = new FileInfo(path); }
            catch { Report("error"); continue; }

            if (!fi.Exists) { Report("skipped"); continue; }

            var tracked = _tracker.GetTrackedFile(path);
            if (tracked is not null)
            {
                bool isStale = fi.LastWriteTimeUtc > tracked.LastModified.ToUniversalTime();
                if (!isStale) { Report("skipped"); continue; }

                // Delete old chunks before re-ingesting
                _vectors.DeleteChunks(tracked.VecRowIds);
            }

            // ── Extract ───────────────────────────────────────────────────
            Report("extracting");
            var text = _extractor.Extract(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                // No IFilter for this extension — still track the file so we
                // don't retry it on every search, but with 0 chunks.
                _tracker.MarkIngested(path, fi.Length, fi.LastWriteTimeUtc, 0, []);
                Report("skipped");
                continue;
            }

            // ── Chunk ─────────────────────────────────────────────────────
            Report("chunking");
            var chunks = Chunker.Chunk(text, _config.ChunkSize, _config.ChunkOverlap).ToList();
            if (chunks.Count == 0) { Report("skipped"); continue; }

            // ── Embed + store ─────────────────────────────────────────────

            // First we need a file_id — MarkIngested with 0 chunks to get the id,
            // then update after we have all row IDs.
            var tempRecord  = _tracker.MarkIngested(path, fi.Length, fi.LastWriteTimeUtc, 0, []);
            var rowIds      = new List<long>(chunks.Count);

            for (int c = 0; c < chunks.Count; c++)
            {
                ct.ThrowIfCancellationRequested();
                Report("embedding", c + 1);

                float[] embedding = await _ollama.EmbedAsync(chunks[c], ct);
                long rowId = _vectors.InsertChunk(tempRecord.Id, c, chunks[c], embedding);
                rowIds.Add(rowId);

                Report("storing", c + 1);
            }

            // Update tracker with final chunk count and all vec row IDs
            _tracker.MarkIngested(path, fi.Length, fi.LastWriteTimeUtc, chunks.Count, [.. rowIds]);
            Report("done", chunks.Count);
        }
    }
}
