using System.Text.Json;
using System.Text.Json.Nodes;

namespace Recall.Cli;

/// <summary>
/// Wraps the runtime configuration values and supports writing
/// the SearchPaths array back to appsettings.json after /setup.
/// </summary>
public sealed class AppConfig
{
    private readonly string _settingsPath;

    /// <summary>Resolved, env-expanded search paths.</summary>
    public string[] SearchPaths { get; set; } = [];

    public string DbPath          { get; init; } = string.Empty;
    public string Vec0DllPath     { get; init; } = string.Empty;
    public string EverythingDllPath { get; init; } = string.Empty;

    public AppConfig(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    /// <summary>
    /// Persist the current SearchPaths back to appsettings.json.
    /// Only the Recall.SearchPaths array is modified; all other keys are preserved.
    /// </summary>
    public async Task SaveAsync()
    {
        JsonNode? root;

        if (File.Exists(_settingsPath))
        {
            var raw = await File.ReadAllTextAsync(_settingsPath);
            root = JsonNode.Parse(raw) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        // Navigate/create Recall section
        if (root["Recall"] is not JsonObject recallSection)
        {
            recallSection = new JsonObject();
            root["Recall"] = recallSection;
        }

        // Replace the SearchPaths array in-place
        recallSection["SearchPaths"] = new JsonArray(
            SearchPaths.Select(p => JsonValue.Create(p)).ToArray<JsonNode?>());

        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(_settingsPath, root.ToJsonString(options));
    }
}
