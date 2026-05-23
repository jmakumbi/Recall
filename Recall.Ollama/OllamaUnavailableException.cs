namespace Recall.Ollama;

/// <summary>
/// Thrown when the Ollama HTTP server cannot be reached (connection refused,
/// network error, or unexpected non-success response on a critical endpoint).
/// The REPL catches this and shows a friendly "start Ollama" message.
/// </summary>
public sealed class OllamaUnavailableException : Exception
{
    public OllamaUnavailableException(string message) : base(message) { }
    public OllamaUnavailableException(string message, Exception inner) : base(message, inner) { }
}
