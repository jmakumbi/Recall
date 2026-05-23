namespace Recall.Storage;

internal static class Schema
{
    internal const string Ddl = """
        CREATE TABLE IF NOT EXISTS ingested_files (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            path          TEXT    NOT NULL UNIQUE,
            size_bytes    INTEGER NOT NULL,
            last_modified TEXT    NOT NULL,
            chunk_count   INTEGER NOT NULL DEFAULT 0,
            ingested_at   TEXT    NOT NULL,
            wds_kind      TEXT,
            vec_row_ids   TEXT    NOT NULL DEFAULT '[]'
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS vec_chunks USING vec0(
            embedding float[768]
        );

        CREATE TABLE IF NOT EXISTS chunks (
            rowid       INTEGER PRIMARY KEY,
            file_id     INTEGER NOT NULL REFERENCES ingested_files(id),
            chunk_index INTEGER NOT NULL,
            text        TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS conversations (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            started_at TEXT    NOT NULL,
            messages   TEXT    NOT NULL DEFAULT '[]'
        );

        CREATE TABLE IF NOT EXISTS kb_stats (
            id           INTEGER PRIMARY KEY CHECK (id = 1),
            total_files  INTEGER NOT NULL DEFAULT 0,
            total_chunks INTEGER NOT NULL DEFAULT 0,
            last_updated TEXT
        );

        INSERT OR IGNORE INTO kb_stats (id, total_files, total_chunks) VALUES (1, 0, 0);
        """;
}
