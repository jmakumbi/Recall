using Recall.Ollama;

var config = new OllamaConfig();
using var client = new OllamaClient(config);

Console.WriteLine("=== Recall.Ollama smoke test ===");
Console.WriteLine($"BaseUrl        : {config.BaseUrl}");
Console.WriteLine($"EmbeddingModel : {config.EmbeddingModel}");
Console.WriteLine($"ChatModel      : {config.ChatModel}");
Console.WriteLine();

// ── 1. Health check ───────────────────────────────────────────────────────
Console.Write("Health check ... ");
OllamaHealthResult health;
try
{
    health = await client.HealthCheckAsync();
}
catch (OllamaUnavailableException ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"FAIL\n{ex.Message}");
    Console.ResetColor();
    return;
}

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("OK");
Console.ResetColor();
Console.WriteLine($"  Installed models : {string.Join(", ", health.InstalledModels)}");

if (!health.EmbeddingModelReady)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"  WARNING: '{config.EmbeddingModel}' not found. Pull it with: ollama pull {config.EmbeddingModel}");
    Console.ResetColor();
}
else Console.WriteLine($"  {config.EmbeddingModel} : ready");

if (!health.ChatModelReady)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"  WARNING: '{config.ChatModel}' not found. Pull it with: ollama pull {config.ChatModel}");
    Console.ResetColor();
}
else Console.WriteLine($"  {config.ChatModel} : ready");

Console.WriteLine();

// ── 2. Embed ──────────────────────────────────────────────────────────────
if (health.EmbeddingModelReady)
{
    Console.Write("Embed test ... ");
    try
    {
        var embedding = await client.EmbedAsync("The quick brown fox jumps over the lazy dog.");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"OK  ({embedding.Length}-dim, first 4: [{string.Join(", ", embedding[..4].Select(f => f.ToString("F4")))}])");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAIL: {ex.Message}");
        Console.ResetColor();
    }
    Console.WriteLine();
}

// ── 3. Chat (streaming) ───────────────────────────────────────────────────
// Fall back to first available non-embedding model if configured one is absent
var chatModelToUse = health.ChatModelReady
    ? config.ChatModel
    : health.InstalledModels
        .FirstOrDefault(m => !m.StartsWith("nomic-embed") && !m.StartsWith("mxbai-embed"));

if (chatModelToUse is not null)
{
    if (!health.ChatModelReady)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Chat test: falling back to '{chatModelToUse}' ('{config.ChatModel}' not pulled)");
        Console.ResetColor();
    }

    var testConfig = health.ChatModelReady ? config : new OllamaConfig
    {
        BaseUrl        = config.BaseUrl,
        EmbeddingModel = config.EmbeddingModel,
        ChatModel      = chatModelToUse,
        ChatContextWindow = config.ChatContextWindow
    };
    using var testClient = health.ChatModelReady ? null : new OllamaClient(testConfig);
    var chatClient = (OllamaClient)(testClient ?? client);

    Console.WriteLine("Chat stream test (one-sentence reply expected) ...");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("  ▸ recall: ");
    try
    {
        var messages = new[]
        {
            ChatMessage.System(OllamaClient.SystemPrompt),
            ChatMessage.User("Context:\n[Source: notes.txt]\nThe capital of France is Paris.\n---\n\nQuestion: What is the capital of France?")
        };

        await foreach (var token in chatClient.ChatAsync(messages))
        {
            Console.Write(token);
        }
        Console.WriteLine();
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\nChat stream: OK");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAIL: {ex.Message}");
        Console.ResetColor();
    }
}

Console.WriteLine("\nDone.");
