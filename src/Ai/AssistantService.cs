using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;

namespace PasMigration.Ai;

// ── Tool catalog ──────────────────────────────────────────────────────────────────────

public sealed record ToolEntry(string Name, string Description);

/// <summary>
/// Singleton that caches catalog embeddings. Only used if embedding router is active.
/// </summary>
public sealed class AssistantCatalog(ILlmProvider provider, ILogger<AssistantCatalog> log)
{
    public static readonly ToolEntry[] Entries =
    [
        new("check_prerequisites",
            "Check whether migration prerequisites are met: connections tested, inventory captured, platform unified, UVA mode, admin roles, OAuth2 app"),
        new("migration_stats",
            "Show migration progress: how many secrets and accounts migrated, percentage complete, migration job history and dates"),
        new("environment_summary",
            "Summarise the source vault size: total secrets, accounts, managed account count, recommend a migration approach"),
        new("reconciliation_status",
            "Show reconciliation results: matched, source-only, target-only, or conflicted items between source and target"),
        new("recent_activity",
            "Show recent activity and event log: actions taken, outcomes, errors in this engagement"),
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
            log.LogInformation("Computing catalog embeddings ({Count} tools)...", Entries.Length);
            var embeddings = new float[Entries.Length][];
            for (int i = 0; i < Entries.Length; i++)
                embeddings[i] = await provider.EmbedAsync(Entries[i].Description, ct);
            _cache = embeddings;
            return _cache;
        }
        finally { _lock.Release(); }
    }
}

// ── Keyword router (fast, no model call) ─────────────────────────────────────────────

/// <summary>
/// Routes questions to tools using keyword matching — no embedding call needed.
/// This saves ~2-5s per request on CPU compared to the embedding router.
/// Falls back to null (general/help response) if nothing matches.
/// </summary>
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
            "how should", "overview", "summary", "what do i have", "inventory"
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

    public static string? Route(string question)
    {
        var q = question.ToLowerInvariant();

        // Help/general — no tool needed
        if (q.TrimStart().StartsWith("help") || q == "help")
            return null;

        string? best = null;
        int bestScore = 0;

        foreach (var (tool, keywords) in Rules)
        {
            int score = keywords.Count(kw => q.Contains(kw));
            if (score > bestScore) { bestScore = score; best = tool; }
        }

        return bestScore > 0 ? best : null;
    }
}

// ── AssistantRouter (keeps embedding path available but unused by default) ────────────

public sealed class AssistantRouter(ILlmProvider provider, AssistantCatalog catalog, ILogger<AssistantRouter> log)
{
    public Task<string?> RouteAsync(string question, CancellationToken ct)
    {
        // Keyword routing — instant, no model call
        var result = KeywordRouter.Route(question);
        log.LogDebug("Keyword router: tool={Tool}", result ?? "none");
        return Task.FromResult(result);
    }
}

// ── AssistantService — streaming orchestrator ─────────────────────────────────────────

public sealed class AssistantService(
    OllamaProvider ollama,
    AssistantRouter router,
    IDbConnection db,
    ILogger<AssistantService> log)
{
    /// <summary>
    /// Streams SSE events:
    ///   data: {"type":"phase","phase":"routing","detail":"..."}
    ///   data: {"type":"tool","tool":"migration_stats"}
    ///   data: {"type":"token","text":"Here is..."}
    ///   data: {"type":"done"}
    ///   data: {"type":"error","message":"..."}
    /// </summary>
    public async IAsyncEnumerable<string> AskStreamAsync(
        Guid engagementId,
        string question,
        IReadOnlyList<ConversationTurn>? history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Phase 1: Route (instant with keyword router)
        yield return Phase("routing", "Identifying relevant data...");

        string? toolName = null;
        try { toolName = await router.RouteAsync(question, ct); }
        catch (Exception ex) { log.LogWarning("Router failed: {Message}", ex.Message); }

        if (toolName is not null)
            yield return Tool(toolName);

        // Phase 2: Run tool (DB query — fast)
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
                toolResult = new { error = "Tool query failed: " + ex.Message };
            }
        }

        // Phase 3: Generate (slow — model inference on CPU)
        yield return Phase("generating", "Generating response (CPU inference)...");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, AssistantPrompt.System)
        };

        if (history is { Count: > 0 })
        {
            foreach (var t in history.TakeLast(4))
            {
                messages.Add(new(ChatRole.User, t.Question));
                messages.Add(new(ChatRole.Assistant, t.Answer));
            }
        }

        string userMessage = toolResult is not null
            ? "User question: " + question + "\n\nTool: " + toolName + "\nData:\n" +
              JsonSerializer.Serialize(toolResult, new JsonSerializerOptions { WriteIndented = true }) +
              "\n\nNarrate this data concisely. Use bullet points. Do not invent anything not in the data."
            : "User question: " + question +
              "\n\nAnswer from your knowledge of the PAS to Secret Server migration tool. If the user says 'help', list your five capabilities briefly.";

        messages.Add(new(ChatRole.User, userMessage));

        await foreach (var token in ollama.ChatStreamAsync(messages, ct))
        {
            if (ct.IsCancellationRequested) yield break;
            yield return Token(token);
        }

        yield return Done();
    }

    // ── SSE helpers — simple string concat, no interpolation ambiguity ────────────────

    private static string Phase(string phase, string detail) =>
        "data: {\"type\":\"phase\",\"phase\":" + JsonSerializer.Serialize(phase) +
        ",\"detail\":" + JsonSerializer.Serialize(detail) + "}\n\n";

    private static string Tool(string tool) =>
        "data: {\"type\":\"tool\",\"tool\":" + JsonSerializer.Serialize(tool) + "}\n\n";

    private static string Token(string text) =>
        "data: {\"type\":\"token\",\"text\":" + JsonSerializer.Serialize(text) + "}\n\n";

    private static string Done() =>
        "data: {\"type\":\"done\"}\n\n";

    private static string Err(string message) =>
        "data: {\"type\":\"error\",\"message\":" + JsonSerializer.Serialize(message) + "}\n\n";
}

// ── DTOs ──────────────────────────────────────────────────────────────────────────────

public sealed record ConversationTurn(string Question, string Answer);
public sealed record AssistantRequest(string Question, IReadOnlyList<ConversationTurn>? History);
