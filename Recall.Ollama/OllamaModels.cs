using System.Text.Json.Serialization;

namespace Recall.Ollama;

// ── Config ────────────────────────────────────────────────────────────────

public sealed class OllamaConfig
{
    public string BaseUrl       { get; init; } = "http://localhost:11434";
    public string EmbeddingModel { get; init; } = "nomic-embed-text";
    public string ChatModel      { get; init; } = "qwen3:8b";
    public int    ChatContextWindow { get; init; } = 8192;
}

// ── Chat messages ─────────────────────────────────────────────────────────

public sealed class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "";

    [JsonPropertyName("content")]
    public string Content { get; init; } = "";

    public static ChatMessage System(string content)    => new() { Role = "system",    Content = content };
    public static ChatMessage User(string content)      => new() { Role = "user",      Content = content };
    public static ChatMessage Assistant(string content) => new() { Role = "assistant", Content = content };
}

// ── Health check ──────────────────────────────────────────────────────────

public sealed class OllamaHealthResult
{
    public bool IsAvailable         { get; init; }
    public bool EmbeddingModelReady { get; init; }
    public bool ChatModelReady      { get; init; }
    public IReadOnlyList<string> InstalledModels { get; init; } = [];

    public bool FullyReady => IsAvailable && EmbeddingModelReady && ChatModelReady;
}

// ── Internal DTOs (not exposed beyond this project) ───────────────────────

internal sealed class TagsResponse
{
    [JsonPropertyName("models")]
    public List<ModelInfo> Models { get; init; } = [];
}

internal sealed class ModelInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
}

internal sealed class EmbedRequest
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = "";
}

internal sealed class EmbedResponse
{
    [JsonPropertyName("embedding")]
    public float[] Embedding { get; init; } = [];
}

internal sealed class ChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("stream")]
    public bool Stream { get; init; } = true;

    [JsonPropertyName("messages")]
    public IEnumerable<ChatMessage> Messages { get; init; } = [];

    [JsonPropertyName("options")]
    public ChatOptions? Options { get; init; }
}

internal sealed class ChatOptions
{
    [JsonPropertyName("num_ctx")]
    public int NumCtx { get; init; }
}

internal sealed class ChatStreamChunk
{
    [JsonPropertyName("message")]
    public ChatMessage? Message { get; init; }

    [JsonPropertyName("done")]
    public bool Done { get; init; }
}
