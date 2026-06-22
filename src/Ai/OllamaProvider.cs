using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PasMigration.Ai;

/// <summary>
/// Ollama implementation of ILlmProvider.
/// Chat  → /api/chat        (streaming)
/// Embed → /api/embeddings
/// Config: Ai__Ollama__Endpoint, Ai__Ollama__ChatModel, Ai__Ollama__EmbedModel
/// </summary>
public sealed class OllamaProvider : ILlmProvider
{
    private readonly IHttpClientFactory _factory;
    private readonly string _chatModel;
    private readonly string _embedModel;
    private readonly ILogger<OllamaProvider> _log;

    public string Name => "ollama";

    public OllamaProvider(IHttpClientFactory factory, IConfiguration cfg, ILogger<OllamaProvider> log)
    {
        _factory    = factory;
        _chatModel  = cfg["Ai__Ollama__ChatModel"]  ?? "llama3.1:8b";
        _embedModel = cfg["Ai__Ollama__EmbedModel"] ?? "nomic-embed-text";
        _log        = log;
    }

    // Non-streaming chat (used by AssistantService after tool data is ready).
    public async Task<ChatResult> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken ct = default)
    {
        var http = _factory.CreateClient("ollama");
        var payload = BuildPayload(messages, stream: false);
        _log.LogDebug("Ollama chat → model={Model} messages={Count}", _chatModel, messages.Count);

        var res = await http.PostAsJsonAsync("/api/chat", payload, ct);
        res.EnsureSuccessStatusCode();

        var doc = await res.Content.ReadFromJsonAsync<JsonDocument>(ct)
                  ?? throw new InvalidOperationException("Empty response from Ollama");

        var content = doc.RootElement
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;

        return new ChatResult(content, Array.Empty<ToolCall>());
    }

    // Streaming chat — yields tokens as they arrive from Ollama.
    public async IAsyncEnumerable<string> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var http = _factory.CreateClient("ollama");
        var payload = BuildPayload(messages, stream: true);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(payload)
        };

        var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();

        using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(line); }
            catch { continue; }

            using (doc)
            {
                if (doc.RootElement.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var content))
                {
                    var token = content.GetString();
                    if (!string.IsNullOrEmpty(token))
                        yield return token;
                }

                // Ollama sets done=true on the final chunk
                if (doc.RootElement.TryGetProperty("done", out var done) && done.GetBoolean())
                    yield break;
            }
        }
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var http = _factory.CreateClient("ollama");
        var payload = new { model = _embedModel, prompt = text };

        var res = await http.PostAsJsonAsync("/api/embeddings", payload, ct);
        res.EnsureSuccessStatusCode();

        var doc = await res.Content.ReadFromJsonAsync<JsonDocument>(ct)
                  ?? throw new InvalidOperationException("Empty embed response from Ollama");

        return doc.RootElement
            .GetProperty("embedding")
            .EnumerateArray()
            .Select(e => e.GetSingle())
            .ToArray();
    }

    private object BuildPayload(IReadOnlyList<ChatMessage> messages, bool stream) => new
    {
        model  = _chatModel,
        stream,
        options = new { num_predict = 1024 },   // cap token output — keeps CPU time sane
        messages = messages.Select(m => new
        {
            role = m.Role switch
            {
                ChatRole.System    => "system",
                ChatRole.User      => "user",
                ChatRole.Assistant => "assistant",
                ChatRole.Tool      => "tool",
                _                  => "user"
            },
            content = m.Content
        }).ToArray()
    };
}
