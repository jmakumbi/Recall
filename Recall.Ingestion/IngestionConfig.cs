namespace Recall.Ingestion;

public sealed class IngestionConfig
{
    public int ChunkSize               { get; init; } = 512;
    public int ChunkOverlap            { get; init; } = 100;
    public int MaxExtractedCharsPerFile { get; init; } = 500_000;
}

public sealed record IngestionProgress(
    string FilePath,
    string FileName,
    int    FileIndex,
    int    TotalFiles,
    int    ChunksEmitted,
    string Phase   // "extracting" | "chunking" | "embedding" | "storing" | "done" | "skipped" | "error"
);
