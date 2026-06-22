using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PasMigration.Ai;

/// <summary>
/// Ollama implementation of ILlmProvider using Ollama's OpenAI-compatible REST API.
/// Chat → /api/chat   Embed → /api/embeddings
/// Config keys: Ai__Ollama__Endpoint, Ai__Ollama__ChatModel, Ai__Ollama__EmbedModel
/// </summary>
public sealed class OllamaProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly string _chatModel;
    private readonly string _embedModel;
    private readonly ILogger<OllamaProvider> _log;

    public string Name => "ollama";

    public OllamaProvider(IHttpClientFactory factory, IConfiguration cfg, ILogger<OllamaProvider> log)
    {
        _http = factory.CreateClient("ollama");
        _chatModel  = cfg["Ai__Ollama__ChatModel"]  ?? "llama3.1:8b";
        _embedModel = cfg["Ai__Ollama__EmbedModel"] ?? "nomic-embed-text";
        _log = log;
    }

    public async Task<ChatResult> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            model = _chatModel,
            stream = false,
            messages = messages.Select(m => new
            {
                role = m.Role switch
                {
                    ChatRole.System    => "system",
                    ChatRole.User      => "user",
                    ChatRole.Assistant => "assistant",
                    ChatRole.Tool      => "tool",
                    _ => "user"
                },
                content = m.Content
            }).ToArray()
        };

        _log.LogDebug("Ollama chat → model={Model} messages={Count}", _chatModel, messages.Count);

        var res = await _http.PostAsJsonAsync("/api/chat", payload, ct);
        res.EnsureSuccessStatusCode();

        var doc = await res.Content.ReadFromJsonAsync<JsonDocument>(ct)
                  ?? throw new InvalidOperationException("Empty response from Ollama");

        var content = doc.RootElement
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;

        return new ChatResult(content, Array.Empty<ToolCall>());
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var payload = new { model = _embedModel, prompt = text };

        var res = await _http.PostAsJsonAsync("/api/embeddings", payload, ct);
        res.EnsureSuccessStatusCode();

        var doc = await res.Content.ReadFromJsonAsync<JsonDocument>(ct)
                  ?? throw new InvalidOperationException("Empty embed response from Ollama");

        return doc.RootElement
            .GetProperty("embedding")
            .EnumerateArray()
            .Select(e => e.GetSingle())
            .ToArray();
    }
}
