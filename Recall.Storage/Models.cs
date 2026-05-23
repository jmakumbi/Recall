namespace Recall.Storage;

public record TrackedFile(
    int Id,
    string Path,
    long SizeBytes,
    DateTime LastModified,
    int ChunkCount,
    DateTime IngestedAt,
    string? WdsKind,
    long[] VecRowIds
);

public record KbStats(
    int TotalFiles,
    int TotalChunks,
    DateTime? LastUpdated
);

public record ChunkRow(
    long RowId,
    int FileId,
    int ChunkIndex,
    string Text,
    string FilePath,
    string? WdsKind,
    float Distance
);
