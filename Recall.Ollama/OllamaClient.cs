using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Recall.Ollama;

public sealed class OllamaClient : IDisposable
{
    public const string SystemPrompt =
        "You are a personal knowledge assistant. You answer questions strictly based on " +
        "the provided context from the user's own files. If the context does not contain " +
        "enough information to answer, say so clearly. Do not speculate beyond what the " +
        "context supports. When referencing information, mention the source file name.";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly OllamaConfig _config;

    public OllamaClient(OllamaConfig config)
    {
        _config = config;
        _http = new HttpClient { BaseAddress = new Uri(config.BaseUrl) };
        _http.Timeout = TimeSpan.FromMinutes(5); // embedding large chunks can be slow
    }

    // ── Health check ─────────────────────────────────────────────────────

    public async Task<OllamaHealthResult> HealthCheckAsync(CancellationToken ct = default)
    {
        TagsResponse? tags;
        try
        {
            tags = await _http.GetFromJsonAsync<TagsResponse>("/api/tags", JsonOpts, ct);
        }
        catch (HttpRequestException ex) when (IsConnectionRefused(ex))
        {
            throw new OllamaUnavailableException(
                $"Cannot reach Ollama at {_config.BaseUrl}. Start it with: ollama serve", ex);
        }
        catch (Exception ex) when (ex is not OllamaUnavailableException)
        {
            throw new OllamaUnavailableException(
                $"Ollama health check failed: {ex.Message}", ex);
        }

        var installedModels = (tags?.Models ?? [])
            .Select(m => m.Name)
            .ToList();

        // Model names may have tags like "qwen3:8b" or "nomic-embed-text:latest"
        bool HasModel(string wanted) =>
            installedModels.Any(n => n.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                                  || n.StartsWith(wanted + ":", StringComparison.OrdinalIgnoreCase));

        return new OllamaHealthResult
        {
            IsAvailable          = true,
            EmbeddingModelReady  = HasModel(_config.EmbeddingModel),
            ChatModelReady       = HasModel(_config.ChatModel),
            InstalledModels      = installedModels
        };
    }

    // ── Embed ─────────────────────────────────────────────────────────────

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var req = new EmbedRequest { Model = _config.EmbeddingModel, Prompt = text };

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsJsonAsync("/api/embeddings", req, JsonOpts, ct);
        }
        catch (HttpRequestException ex) when (IsConnectionRefused(ex))
        {
            throw new OllamaUnavailableException(
                $"Ollama is not running. Start it with: ollama serve", ex);
        }

        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<EmbedResponse>(JsonOpts, ct)
                   ?? throw new InvalidOperationException("Empty embedding response from Ollama.");

        if (body.Embedding.Length == 0)
            throw new InvalidOperationException(
                $"Ollama returned an empty embedding. Is '{_config.EmbeddingModel}' pulled?");

        return body.Embedding;
    }

    // ── Chat (streaming) ──────────────────────────────────────────────────

    public async IAsyncEnumerable<string> ChatAsync(
        IEnumerable<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var req = new ChatRequest
        {
            Model    = _config.ChatModel,
            Stream   = true,
            Messages = messages,
            Options  = new ChatOptions { NumCtx = _config.ChatContextWindow }
        };

        HttpResponseMessage resp;
        try
        {
            var reqMsg = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
            {
                Content = JsonContent.Create(req, options: JsonOpts)
            };
            resp = await _http.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex) when (IsConnectionRefused(ex))
        {
            throw new OllamaUnavailableException(
                $"Ollama is not running. Start it with: ollama serve", ex);
        }

        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            ChatStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChatStreamChunk>(line, JsonOpts);
            }
            catch (JsonException)
            {
                continue; // skip malformed lines
            }

            if (chunk is null) continue;
            if (chunk.Done) break;

            var token = chunk.Message?.Content;
            if (!string.IsNullOrEmpty(token))
                yield return token;
        }
    }

    public void Dispose() => _http.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool IsConnectionRefused(HttpRequestException ex) =>
        ex.InnerException is System.Net.Sockets.SocketException se &&
        (se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused ||
         se.SocketErrorCode == System.Net.Sockets.SocketError.HostNotFound);
}
