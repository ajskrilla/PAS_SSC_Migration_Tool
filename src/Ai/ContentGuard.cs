using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PasMigration.Ai;

/// <summary>Verdict from a Llama-Guard-style safety classifier.</summary>
public sealed record GuardVerdict(bool Safe, string? Category, string RawResponse);

/// <summary>
/// Thin wrapper around a Llama-Guard-compatible Ollama model (default: llama-guard3:1b).
/// Classifies a single user prompt, or a completed user+assistant exchange, as safe/unsafe
/// against the MLCommons hazard taxonomy Llama Guard is trained on.
///
/// This is a classifier, not a chat participant — no streaming, no tool calls, no conversation
/// history. It reuses the same "ollama" named HttpClient as OllamaProvider, just pointed at a
/// much smaller model, so it stays cheap even run on every slow-path question and answer.
///
/// Applied on the slow path only (explain_failures + open-ended questions) — the fast,
/// keyword-routed data lookups never reach a generative model at all, so there's nothing there
/// for a content guard to check.
/// </summary>
public sealed class ContentGuard
{
    private readonly IHttpClientFactory _factory;
    private readonly string _guardModel;
    private readonly ILogger<ContentGuard> _log;

    public ContentGuard(IHttpClientFactory factory, IConfiguration cfg, ILogger<ContentGuard> log)
    {
        _factory = factory;
        _guardModel = cfg["Ai:Ollama:GuardModel"]
                   ?? Environment.GetEnvironmentVariable("OLLAMA_GUARD_MODEL")
                   ?? "llama-guard3:1b";
        _log = log;
        _log.LogInformation("ContentGuard init: model={Model}", _guardModel);
    }

    /// <summary>Classifies a single user prompt, before it reaches the main model.</summary>
    public Task<GuardVerdict> CheckPromptAsync(string question, CancellationToken ct = default) =>
        ClassifyAsync(new object[] { new { role = "user", content = question } }, ct);

    /// <summary>
    /// Classifies a completed assistant answer, given the question it answered. Llama Guard's
    /// chat template classifies whichever message is LAST in the list, so — unlike the prompt
    /// check — the assistant turn must be included for this to check the answer rather than
    /// re-checking the question.
    /// </summary>
    public Task<GuardVerdict> CheckResponseAsync(string question, string answer, CancellationToken ct = default) =>
        ClassifyAsync(new object[]
        {
            new { role = "user", content = question },
            new { role = "assistant", content = answer },
        }, ct);

    private async Task<GuardVerdict> ClassifyAsync(object[] messages, CancellationToken ct)
    {
        var http = _factory.CreateClient("ollama");
        var payload = new { model = _guardModel, stream = false, messages };

        var res = await http.PostAsJsonAsync("/api/chat", payload, ct);
        res.EnsureSuccessStatusCode();

        var doc = await res.Content.ReadFromJsonAsync<JsonDocument>(ct)
                  ?? throw new InvalidOperationException("Empty response from guard model");

        var text = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        var trimmed = text.Trim();

        if (trimmed.StartsWith("safe", StringComparison.OrdinalIgnoreCase))
            return new GuardVerdict(true, null, trimmed);

        // Unsafe responses are "unsafe\nS<n>" — the category code, if present, is on line two.
        var lines = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var category = lines.Length > 1 ? lines[1] : null;
        _log.LogWarning("ContentGuard flagged content unsafe: {Category}", category ?? "(uncategorized)");
        return new GuardVerdict(false, category, trimmed);
    }
}
