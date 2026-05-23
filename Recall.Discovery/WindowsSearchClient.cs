using System.Data.OleDb;
using System.Runtime.InteropServices;

namespace Recall.Discovery;

/// <summary>
/// Queries Windows Desktop Search via the OLE DB provider.
/// Results enrich Everything hits with AutoSummary snippets and Kind metadata.
/// </summary>
public sealed class WindowsSearchClient
{
    private const string ConnectionString =
        "Provider=Search.CollatorDSO.1;Extended Properties='Application=Windows'";

    private const int MaxWdsResults = 50;

    private readonly DiscoveryConfig _config;

    public WindowsSearchClient(DiscoveryConfig config) => _config = config;

    /// <summary>
    /// Run a full-text search against the Windows Search index.
    /// Returns at most 50 results. Never throws — returns empty on any failure.
    /// </summary>
    public IReadOnlyList<DiscoveryResult> Search(string query)
    {
        var scope = Environment.ExpandEnvironmentVariables(_config.DefaultSearchScope)
                               .TrimEnd('\\') + "\\%";
        var results = new List<DiscoveryResult>();

        try
        {
            using var conn = new OleDbConnection(ConnectionString);
            conn.Open();

            // Search.CollatorDSO.1 does not implement ICommandWithParameters,
            // so we must inline values directly. Escape single quotes by doubling them.
            var safeQuery = query.Replace("'", "''");
            var safeScope = scope.Replace("'", "''");

            // CONTAINS phrase syntax: wrap multi-word queries in double-quotes inside the string literal
            var containsArg = query.Contains(' ')
                ? $"'\"{ safeQuery }\"'"
                : $"'{safeQuery}'";

            var sql = $"""
                SELECT TOP 50
                    System.ItemPathDisplay,
                    System.Search.AutoSummary,
                    System.Kind,
                    System.DateModified
                FROM SystemIndex
                WHERE CONTAINS(*, {containsArg})
                  AND System.ItemPathDisplay LIKE '{safeScope}'
                ORDER BY System.DateModified DESC
                """;

            using var cmd    = new OleDbCommand(sql, conn);
            using var reader = cmd.ExecuteReader();
            int count = 0;
            while (reader.Read() && count < MaxWdsResults)
            {
                var path    = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (path is null) continue;

                var snippet = reader.IsDBNull(1) ? null : reader.GetString(1);
                var kind    = reader.IsDBNull(2) ? null : NormaliseKind(reader.GetValue(2));

                DateTime modified = DateTime.MinValue;
                if (!reader.IsDBNull(3) && reader.GetValue(3) is DateTime dt)
                    modified = dt;

                var fileName  = Path.GetFileName(path);
                var extension = Path.GetExtension(path).ToLowerInvariant();

                results.Add(new DiscoveryResult(
                    path, fileName, extension,
                    0, modified,          // size not available from WDS
                    snippet, kind,
                    false, false));

                count++;
            }
        }
        catch (Exception ex) when (ex is OleDbException or InvalidOperationException or COMException)
        {
            // WDS unavailable or query failed — degrade gracefully
            Console.Error.WriteLine($"[recall] Windows Search unavailable: {ex.Message}");
        }

        return results;
    }

    private static string? NormaliseKind(object raw)
    {
        // System.Kind can be a string[] or a plain string depending on the provider version
        return raw switch
        {
            string s   => s,
            string[] a => a.Length > 0 ? a[0] : null,
            _          => raw?.ToString()
        };
    }
}
