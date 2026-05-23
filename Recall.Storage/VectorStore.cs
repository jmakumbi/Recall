using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Recall.Storage;

public sealed class VectorStore
{
    private readonly SqliteConnection _conn;

    public VectorStore(TrackerDb db) => _conn = db.Connection;

    public long InsertChunk(int fileId, int chunkIndex, string text, float[] embedding)
    {
        var embBytes = FloatsToBytes(embedding);

        using var cmd1 = _conn.CreateCommand();
        cmd1.CommandText = "INSERT INTO vec_chunks (embedding) VALUES (vec_f32($emb))";
        cmd1.Parameters.AddWithValue("$emb", embBytes);
        cmd1.ExecuteNonQuery();

        using var cmd2 = _conn.CreateCommand();
        cmd2.CommandText = "SELECT last_insert_rowid()";
        var rowId = (long)(cmd2.ExecuteScalar() ?? throw new InvalidOperationException("No rowid returned"));

        using var cmd3 = _conn.CreateCommand();
        cmd3.CommandText = "INSERT INTO chunks (rowid, file_id, chunk_index, text) VALUES ($rid, $fid, $ci, $txt)";
        cmd3.Parameters.AddWithValue("$rid", rowId);
        cmd3.Parameters.AddWithValue("$fid", fileId);
        cmd3.Parameters.AddWithValue("$ci", chunkIndex);
        cmd3.Parameters.AddWithValue("$txt", text);
        cmd3.ExecuteNonQuery();

        return rowId;
    }

    public void DeleteChunks(long[] rowIds)
    {
        foreach (var id in rowIds)
        {
            using var del1 = _conn.CreateCommand();
            del1.CommandText = "DELETE FROM chunks WHERE rowid = $rid";
            del1.Parameters.AddWithValue("$rid", id);
            del1.ExecuteNonQuery();

            using var del2 = _conn.CreateCommand();
            del2.CommandText = "DELETE FROM vec_chunks WHERE rowid = $rid";
            del2.Parameters.AddWithValue("$rid", id);
            del2.ExecuteNonQuery();
        }
    }

    public List<ChunkRow> Search(float[] queryEmbedding, int topK, float minScore)
    {
        var embBytes = FloatsToBytes(queryEmbedding);
        var results = new List<ChunkRow>();

        using var cmd = _conn.CreateCommand();
        // vec0 distance is only accessible in the virtual table's own SELECT;
        // use a CTE to capture rowid+distance before joining to regular tables.
        cmd.CommandText = """
            WITH knn AS (
                SELECT rowid, distance
                FROM vec_chunks
                WHERE embedding MATCH vec_f32($emb)
                  AND k = $k
                ORDER BY distance
            )
            SELECT c.rowid, c.file_id, c.chunk_index, c.text,
                   f.path, f.wds_kind,
                   knn.distance
            FROM knn
            JOIN chunks c ON c.rowid = knn.rowid
            JOIN ingested_files f ON f.id = c.file_id
            ORDER BY knn.distance
            """;
        cmd.Parameters.AddWithValue("$emb", embBytes);
        cmd.Parameters.AddWithValue("$k", topK);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var distance = (float)r.GetDouble(6);
            if (distance > minScore) continue;
            results.Add(new ChunkRow(
                r.GetInt64(0),
                r.GetInt32(1),
                r.GetInt32(2),
                r.GetString(3),
                r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                distance));
        }

        return results;
    }

    private static byte[] FloatsToBytes(float[] floats) =>
        MemoryMarshal.AsBytes(floats.AsSpan()).ToArray();
}
