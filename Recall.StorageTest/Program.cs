using System.Runtime.InteropServices;
using Recall.Storage;

var dbPath = Path.Combine(Path.GetTempPath(), $"recall_test_{Guid.NewGuid():N}.db");
var vec0Path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "libs", "vec0.dll"));

Console.WriteLine("=== Recall.Storage smoke test ===");
Console.WriteLine($"DB:   {dbPath}");
Console.WriteLine($"vec0: {vec0Path} (exists: {File.Exists(vec0Path)})");

if (!File.Exists(vec0Path))
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("SKIP: vec0.dll not found in libs\\ — place it there and re-run.");
    Console.ResetColor();
    return;
}

try
{
    using var db = TrackerDb.OpenAndInit(dbPath, vec0Path);
    Console.WriteLine("OK: database opened and schema initialised");

    var stats = db.GetKbStats();
    Console.WriteLine($"OK: KB stats — files={stats.TotalFiles}, chunks={stats.TotalChunks}");

    var fakePath = @"C:\Users\test\Documents\sample.docx";
    var tf = db.MarkIngested(fakePath, 12345, DateTime.UtcNow.AddDays(-1), 0, []);
    Console.WriteLine($"OK: MarkIngested id={tf.Id}");
    Console.WriteLine($"OK: IsIngested={db.IsIngested(fakePath)}");
    Console.WriteLine($"OK: GetTrackedFile id={db.GetTrackedFile(fakePath)?.Id}");

    var vs = new VectorStore(db);
    float[] fakeEmb = new float[768];
    Random.Shared.NextBytes(MemoryMarshal.AsBytes(fakeEmb.AsSpan()));

    var rowId = vs.InsertChunk(tf.Id, 0, "Hello world chunk", fakeEmb);
    Console.WriteLine($"OK: InsertChunk rowId={rowId}");

    db.MarkIngested(fakePath, 12345, DateTime.UtcNow.AddDays(-1), 1, [rowId]);
    var statsAfter = db.GetKbStats();
    Console.WriteLine($"OK: KB stats after — files={statsAfter.TotalFiles}, chunks={statsAfter.TotalChunks}");

    var results = vs.Search(fakeEmb, 5, 1.0f);
    Console.WriteLine($"OK: Search returned {results.Count} result(s)");

    vs.DeleteChunks([rowId]);
    Console.WriteLine("OK: DeleteChunks");

    db.DeleteFile(fakePath);
    Console.WriteLine($"OK: DeleteFile — IsIngested after={db.IsIngested(fakePath)}");

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("\nAll storage tests passed.");
    Console.ResetColor();
}
finally
{
    try { File.Delete(dbPath); } catch { }
}
