namespace Recall.Cli;

/// <summary>
/// Classifies user input into one of the supported intents.
/// </summary>
public enum Intent
{
    /// <summary>A slash-prefixed command, e.g. /help or /ingest query.</summary>
    Command,
    /// <summary>Likely a file discovery query (find/search/list keywords or file extensions).</summary>
    Discovery,
    /// <summary>Likely a knowledge-base chat query.</summary>
    Chat,
    /// <summary>Could be discovery or chat — prompt user to disambiguate.</summary>
    Ambiguous
}

public sealed class IntentClassifier
{
    private static readonly string[] DiscoveryKeywords =
    [
        "find", "search", "list", "show", "locate", "where", "which", "look for",
        "open", "recent", "modified", "created"
    ];

    private static readonly string[] FileExtensions =
    [
        ".docx", ".doc", ".pdf", ".txt", ".xlsx", ".xls",
        ".pptx", ".ppt", ".msg", ".eml", ".csv", ".md"
    ];

    // Words that lean toward knowledge-base chat even if discovery keywords present
    private static readonly string[] ChatKeywords =
    [
        "what", "why", "how", "explain", "summarize", "summarise",
        "tell me", "describe", "compare", "analyse", "analyze",
        "when did", "who is", "does"
    ];

    public (Intent Intent, string? CommandName, string CommandArgs) Classify(string input)
    {
        input = input.Trim();

        if (string.IsNullOrWhiteSpace(input))
            return (Intent.Chat, null, string.Empty);

        // Slash command
        if (input.StartsWith('/'))
        {
            var parts       = input[1..].Split(' ', 2, StringSplitOptions.TrimEntries);
            var commandName = parts[0].ToLowerInvariant();
            var args        = parts.Length > 1 ? parts[1] : string.Empty;
            return (Intent.Command, commandName, args);
        }

        var lower = input.ToLowerInvariant();

        bool hasChatKw  = ChatKeywords.Any(k => lower.Contains(k));
        bool hasFileExt = FileExtensions.Any(e => lower.Contains(e));
        bool hasDiscKw  = DiscoveryKeywords.Any(k => lower.Contains(k));

        // File extension mention → strongly discovery
        if (hasFileExt && !hasChatKw)
            return (Intent.Discovery, null, input);

        // Pure chat keywords without discovery signals → chat
        if (hasChatKw && !hasDiscKw && !hasFileExt)
            return (Intent.Chat, null, input);

        // Both or neither → ambiguous
        if (hasDiscKw && hasChatKw)
            return (Intent.Ambiguous, null, input);

        if (hasDiscKw)
            return (Intent.Discovery, null, input);

        return (Intent.Chat, null, input);
    }
}
