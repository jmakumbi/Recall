namespace Recall.Retrieval;

/// <summary>
/// A single chunk returned by a retrieval query, with enough context
/// for the CLI to display sources and for the LLM to generate a grounded answer.
/// </summary>
public sealed record ChunkResult(
    long   RowId,
    int    FileId,
    int    ChunkIndex,
    string Text,
    string FilePath,
    string FileName,
    string? Kind,
    float  Distance);

/// <summary>
/// Configuration for the retrieval layer.
/// </summary>
public sealed class RetrievalConfig
{
    /// <summary>
    /// Maximum number of chunks to return per query.
    /// </summary>
    public int TopK { get; init; } = 5;

    /// <summary>
    /// Maximum L2 distance between query and chunk embeddings (unit-normalised vectors).
    /// L2 distance = sqrt(2 * (1 - cosine_similarity)), so:
    ///   1.0  ≈ cosine similarity ≥ 0.5  (good semantic match)
    ///   1.18 ≈ cosine similarity ≥ 0.3  (spec minimum — permissive)
    ///   1.41 ≈ cosine similarity ≥ 0.0  (orthogonal — likely noise)
    /// Default 1.0 keeps results with at least moderate semantic similarity.
    /// </summary>
    public float MaxDistance { get; init; } = 1.0f;

    /// <summary>
    /// Maximum total characters of context to assemble for the LLM prompt.
    /// Prevents exceeding the model's context window.
    /// </summary>
    public int MaxContextChars { get; init; } = 6_000;
}
