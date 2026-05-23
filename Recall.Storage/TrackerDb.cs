using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Recall.Storage;

public sealed class TrackerDb : IDisposable
{
    private readonly SqliteConnection _conn;

    private TrackerDb(SqliteConnection conn) => _conn = conn;

    public static TrackerDb OpenAndInit(string dbPath, string vec0DllPath)
    {
        var dir = System.IO.Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            System.IO.Directory.CreateDirectory(dir);

        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        conn.EnableExtensions(true);
        var absVec0 = System.IO.Path.GetFullPath(vec0DllPath);
        conn.LoadExtension(absVec0, "sqlite3_vec_init");

        foreach (var statement in Schema.Ddl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = statement;
            cmd.ExecuteNonQuery();
        }

        return new TrackerDb(conn);
    }

    public bool IsIngested(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM ingested_files WHERE path = $path";
        cmd.Parameters.AddWithValue("$path", NormalisePath(path));
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    public TrackedFile? GetTrackedFile(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, path, size_bytes, last_modified, chunk_count, ingested_at, wds_kind, vec_row_ids
            FROM ingested_files WHERE path = $path
            """;
        cmd.Parameters.AddWithValue("$path", NormalisePath(path));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ReadTrackedFile(r);
    }

    public TrackedFile MarkIngested(
        string path, long sizeBytes, DateTime lastModified,
        int chunkCount, long[] vecRowIds, string? wdsKind = null)
    {
        var normPath = NormalisePath(path);
        var now = DateTime.UtcNow.ToString("o");
        var rowIdsJson = JsonSerializer.Serialize(vecRowIds);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ingested_files (path, size_bytes, last_modified, chunk_count, ingested_at, wds_kind, vec_row_ids)
            VALUES ($path, $size, $lm, $cc, $ia, $kind, $vri)
            ON CONFLICT(path) DO UPDATE SET
                size_bytes    = excluded.size_bytes,
                last_modified = excluded.last_modified,
                chunk_count   = excluded.chunk_count,
                ingested_at   = excluded.ingested_at,
                wds_kind      = excluded.wds_kind,
                vec_row_ids   = excluded.vec_row_ids
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$path", normPath);
        cmd.Parameters.AddWithValue("$size", sizeBytes);
        cmd.Parameters.AddWithValue("$lm", lastModified.ToString("o"));
        cmd.Parameters.AddWithValue("$cc", chunkCount);
        cmd.Parameters.AddWithValue("$ia", now);
        cmd.Parameters.AddWithValue("$kind", wdsKind ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$vri", rowIdsJson);

        var id = (long)(cmd.ExecuteScalar() ?? 0L);

        UpdateKbStats();
        return new TrackedFile(
            (int)id, normPath, sizeBytes, lastModified,
            chunkCount, DateTime.Parse(now), wdsKind, vecRowIds);
    }

    public void DeleteFile(string path)
    {
        var file = GetTrackedFile(path);
        if (file is null) return;

        using var tx = _conn.BeginTransaction();
        try
        {
            using var del1 = _conn.CreateCommand();
            del1.Transaction = tx;
            del1.CommandText = "DELETE FROM chunks WHERE file_id = $fid";
            del1.Parameters.AddWithValue("$fid", file.Id);
            del1.ExecuteNonQuery();

            foreach (var rowId in file.VecRowIds)
            {
                using var del2 = _conn.CreateCommand();
                del2.Transaction = tx;
                del2.CommandText = "DELETE FROM vec_chunks WHERE rowid = $rid";
                del2.Parameters.AddWithValue("$rid", rowId);
                del2.ExecuteNonQuery();
            }

            using var del3 = _conn.CreateCommand();
            del3.Transaction = tx;
            del3.CommandText = "DELETE FROM ingested_files WHERE id = $fid";
            del3.Parameters.AddWithValue("$fid", file.Id);
            del3.ExecuteNonQuery();

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        UpdateKbStats();
    }

    public KbStats GetKbStats()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT total_files, total_chunks, last_updated FROM kb_stats WHERE id = 1";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new KbStats(0, 0, null);
        return new KbStats(
            r.GetInt32(0),
            r.GetInt32(1),
            r.IsDBNull(2) ? null : DateTime.Parse(r.GetString(2)));
    }

    public void UpdateKbStats()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE kb_stats
            SET total_files  = (SELECT COUNT(*) FROM ingested_files),
                total_chunks = (SELECT COUNT(*) FROM chunks),
                last_updated = $now
            WHERE id = 1
            """;
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public SqliteConnection Connection => _conn;

    public void Dispose() => _conn.Dispose();

    private static string NormalisePath(string path) =>
        System.IO.Path.GetFullPath(path).ToLowerInvariant().Replace('/', '\\');

    private static TrackedFile ReadTrackedFile(SqliteDataReader r)
    {
        var rowIds = JsonSerializer.Deserialize<long[]>(r.GetString(7)) ?? [];
        return new TrackedFile(
            r.GetInt32(0),
            r.GetString(1),
            r.GetInt64(2),
            DateTime.Parse(r.GetString(3)),
            r.GetInt32(4),
            DateTime.Parse(r.GetString(5)),
            r.IsDBNull(6) ? null : r.GetString(6),
            rowIds);
    }
}
