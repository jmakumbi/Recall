using Recall.Ollama;
using Recall.Storage;

namespace Recall.Retrieval;

/// <summary>
/// Retrieves semantically relevant chunks for a user query and assembles
/// a context string suitable for the LLM system prompt.
/// </summary>
public sealed class Retriever
{
    private readonly OllamaClient    _ollama;
    private readonly VectorStore     _vectors;
    private readonly RetrievalConfig _config;

    public Retriever(OllamaClient ollama, VectorStore vectors, RetrievalConfig config)
    {
        _ollama  = ollama;
        _vectors = vectors;
        _config  = config;
    }

    /// <summary>
    /// Embed <paramref name="userQuery"/>, search the vector store, and return
    /// the top-K chunks ordered by ascending L2 distance (closest first).
    /// </summary>
    public async Task<List<ChunkResult>> QueryAsync(
        string userQuery,
        CancellationToken ct = default)
    {
        var queryEmbedding = await _ollama.EmbedAsync(userQuery, ct);

        var rawRows = _vectors.Search(queryEmbedding, _config.TopK, _config.MaxDistance);

        return rawRows.Select(r => new ChunkResult(
            r.RowId,
            r.FileId,
            r.ChunkIndex,
            r.Text,
            r.FilePath,
            Path.GetFileName(r.FilePath),
            r.WdsKind,
            r.Distance))
            .ToList();
    }

    /// <summary>
    /// Assemble a context block from <paramref name="chunks"/> for injection into
    /// the LLM prompt, capped at <see cref="RetrievalConfig.MaxContextChars"/>.
    /// </summary>
    /// <remarks>
    /// Format per source file:
    /// <code>
    /// [Source: filename.docx]
    /// chunk text …
    ///
    /// chunk text …
    ///
    /// ---
    /// </code>
    /// </remarks>
    public string AssembleContext(List<ChunkResult> chunks)
    {
        if (chunks.Count == 0) return string.Empty;

        // Group by source file, preserving the order of the closest chunk per file
        var byFile = chunks
            .GroupBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => (FileName: g.First().FileName, Chunks: g.ToList()));

        var sb   = new System.Text.StringBuilder(_config.MaxContextChars);
        int used = 0;

        foreach (var (fileName, fileChunks) in byFile)
        {
            var header = $"[Source: {fileName}]\n";
            if (used + header.Length > _config.MaxContextChars) break;
            sb.Append(header);
            used += header.Length;

            foreach (var chunk in fileChunks)
            {
                var block = chunk.Text + "\n\n";
                if (used + block.Length > _config.MaxContextChars)
                {
                    // Try to fit a truncated slice of this chunk
                    int remaining = _config.MaxContextChars - used;
                    if (remaining > 80)
                    {
                        sb.Append(chunk.Text[..(remaining - 1)]);
                        sb.Append('\n');
                        used = _config.MaxContextChars;
                    }
                    goto Done;
                }
                sb.Append(block);
                used += block.Length;
            }

            sb.Append("---\n");
            used += 4;
        }

        Done:
        return sb.ToString().TrimEnd();
    }
}
