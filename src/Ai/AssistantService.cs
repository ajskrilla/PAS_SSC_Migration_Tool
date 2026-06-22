using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;

namespace PasMigration.Ai;

public sealed record ToolEntry(string Name, string Description);

public sealed class AssistantCatalog(ILlmProvider provider, ILogger<AssistantCatalog> log)
{
    public static readonly ToolEntry[] Entries =
    [
        new("check_prerequisites",  "Check prerequisites, permissions, UVA mode, connections, OAuth2"),
        new("migration_stats",      "Migration progress, percentage, counts, job history, dates"),
        new("environment_summary",  "Vault size, account counts, migration approach recommendation"),
        new("reconciliation_status","Reconciliation diff, matched, source-only, target-only, conflicts"),
        new("recent_activity",      "Recent activity, event log, errors, what happened"),
    ];

    private float[][]? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<float[][]> GetEmbeddingsAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is not null) return _cache;
            log.LogInformation("Computing catalog embeddings...");
            var embeddings = new float[Entries.Length][];
            for (int i = 0; i < Entries.Length; i++)
                embeddings[i] = await provider.EmbedAsync(Entries[i].Description, ct);
            _cache = embeddings;
            return _cache;
        }
        finally { _lock.Release(); }
    }
}

// ── Keyword router — instant, no model call ───────────────────────────────────────────

public static class KeywordRouter
{
    private static readonly (string Tool, string[] Keywords)[] Rules =
    [
        ("check_prerequisites", new[]
        {
            "prerequisite", "prereq", "permission", "uva", "unlimited vault",
            "oauth", "api account", "service account", "admin role", "verify",
            "setup", "ready", "readiness", "configured", "platform enabled"
        }),
        ("migration_stats", new[]
        {
            "percent", "percentage", "progress", "how many", "migrated",
            "stats", "statistics", "count", "how much", "complete", "done",
            "days did we", "when did", "migration date", "job history"
        }),
        ("environment_summary", new[]
        {
            "environment", "vault size", "how big", "approach", "path",
            "recommend", "suggest", "plan", "strategy", "should i",
            "how should", "overview", "what do i have", "inventory", "size"
        }),
        ("reconciliation_status", new[]
        {
            "reconcil", "match", "diff", "difference", "source only",
            "target only", "conflict", "discrepan", "compare"
        }),
        ("recent_activity", new[]
        {
            "recent", "activity", "happening", "event", "log", "last",
            "what happened", "history", "failed", "error", "issue"
        }),
    ];

    /// <summary>
    /// Returns (toolName, useDirectFormat).
    /// useDirectFormat=true means skip the LLM entirely and return structured JSON
    /// for the frontend to render — instant response for data-lookup questions.
    /// useDirectFormat=false means pass tool result through the LLM for narration.
    /// </summary>
    public static (string? Tool, bool Direct) Route(string question)
    {
        var q = question.ToLowerInvariant().Trim();

        // Pure help — no tool, no LLM needed for basic capability listing
        if (q is "help" or "?" or "what can you do" or "capabilities")
            return (null, false);

        string? best = null;
        int bestScore = 0;

        foreach (var (tool, keywords) in Rules)
        {
            int score = keywords.Count(kw => q.Contains(kw));
            if (score > bestScore) { bestScore = score; best = tool; }
        }

        if (best is null) return (null, false);

        // Direct (no LLM) for pure data-lookup questions — these benefit from
        // structured rendering, not prose narration, and are instant.
        bool direct = bestScore >= 1 && (
            best == "check_prerequisites" ||
            best == "migration_stats"     ||
            best == "reconciliation_status"
        );

        return (best, direct);
    }
}

public sealed class AssistantRouter(ILlmProvider provider, AssistantCatalog catalog, ILogger<AssistantRouter> log)
{
    public Task<(string? Tool, bool Direct)> RouteAsync(string question, CancellationToken ct)
    {
        var result = KeywordRouter.Route(question);
        log.LogDebug("Router: tool={Tool} direct={Direct}", result.Tool ?? "none", result.Direct);
        return Task.FromResult(result);
    }
}

// ── AssistantService ──────────────────────────────────────────────────────────────────

public sealed class AssistantService(
    OllamaProvider ollama,
    AssistantRouter router,
    IDbConnection db,
    ILogger<AssistantService> log)
{
    public async IAsyncEnumerable<string> AskStreamAsync(
        Guid engagementId,
        string question,
        IReadOnlyList<ConversationTurn>? history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return Phase("routing", "Identifying relevant data...");

        var (toolName, direct) = await router.RouteAsync(question, ct);

        if (toolName is not null)
            yield return Tool(toolName);

        // Phase 2: fetch from DB
        object? toolResult = null;
        if (toolName is not null)
        {
            yield return Phase("fetching", "Querying migration database...");
            try
            {
                var tools = new AssistantTools(db);
                toolResult = toolName switch
                {
                    "check_prerequisites"   => await tools.CheckPrerequisitesAsync(engagementId),
                    "migration_stats"       => await tools.MigrationStatsAsync(engagementId),
                    "environment_summary"   => await tools.EnvironmentSummaryAsync(engagementId),
                    "reconciliation_status" => await tools.ReconciliationStatusAsync(engagementId),
                    "recent_activity"       => await tools.RecentActivityAsync(engagementId),
                    _                       => null
                };
            }
            catch (Exception ex)
            {
                log.LogError("Tool {Tool} failed: {Message}", toolName, ex.Message);
                toolResult = new { error = "Query failed: " + ex.Message };
                direct = false;
            }
        }

        // Phase 3a: direct — emit structured data, skip LLM entirely
        if (direct && toolResult is not null)
        {
            yield return Phase("rendering", "Formatting results...");
            var json = JsonSerializer.Serialize(toolResult,
                new JsonSerializerOptions { WriteIndented = false });
            yield return DirectData(json);
            yield return Done();
            yield break;
        }

        // Phase 3b: LLM narration — for open-ended questions and environment_summary
        yield return Phase("generating", "Generating response (CPU model)...");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, AssistantPrompt.System)
        };

        if (history is { Count: > 0 })
        {
            foreach (var t in history.TakeLast(2)) // trim to 2 turns for speed
            {
                messages.Add(new(ChatRole.User,      t.Question));
                messages.Add(new(ChatRole.Assistant, t.Answer));
            }
        }

        string userMessage = toolResult is not null
            ? "Question: " + question + "\n\nData from " + toolName + ":\n" +
              JsonSerializer.Serialize(toolResult, new JsonSerializerOptions { WriteIndented = true }) +
              "\n\nGive a concise summary in plain text, 3-5 bullet points max. No markdown."
            : "Question: " + question +
              "\n\nAnswer briefly. If the user says 'help', list your five capabilities in 5 short lines.";

        messages.Add(new(ChatRole.User, userMessage));

        await foreach (var token in ollama.ChatStreamAsync(messages, ct))
        {
            if (ct.IsCancellationRequested) yield break;
            yield return Token(token);
        }

        yield return Done();
    }

    // ── SSE helpers ───────────────────────────────────────────────────────────────────

    private static string Phase(string phase, string detail) =>
        "data: {\"type\":\"phase\",\"phase\":" + JsonSerializer.Serialize(phase) +
        ",\"detail\":" + JsonSerializer.Serialize(detail) + "}\n\n";

    private static string Tool(string tool) =>
        "data: {\"type\":\"tool\",\"tool\":" + JsonSerializer.Serialize(tool) + "}\n\n";

    private static string Token(string text) =>
        "data: {\"type\":\"token\",\"text\":" + JsonSerializer.Serialize(text) + "}\n\n";

    private static string DirectData(string json) =>
        "data: {\"type\":\"direct\",\"data\":" + json + "}\n\n";

    private static string Done() =>
        "data: {\"type\":\"done\"}\n\n";
}

// ── DTOs ──────────────────────────────────────────────────────────────────────────────

public sealed record ConversationTurn(string Question, string Answer);
public sealed record AssistantRequest(string Question, IReadOnlyList<ConversationTurn>? History);
