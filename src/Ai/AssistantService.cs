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
            "what happened", "history"
        }),
        ("explain_failures", new[]
        {
            "fail", "failed", "error", "wrong", "broke", "issue", "problem",
            "why", "what went", "not working", "fix", "debug", "diagnos"
        }),
        ("risk_scan", new[]
        {
            "risk", "scan", "ready", "safe to migrate", "before migrat",
            "check before", "potential issue", "warning", "concern", "duplicate",
            "large file", "problem"
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
            best == "check_prerequisites"   ||
            best == "migration_stats"       ||
            best == "reconciliation_status" ||
            best == "risk_scan"             ||
            best == "environment_summary"
        );
        // explain_failures always goes through LLM for narration

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
    ContentGuard guard,
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
                    "explain_failures"      => await new AssistantTools2(db).ExplainFailuresAsync(engagementId),
                    "risk_scan"             => await new AssistantTools2(db).RiskScanAsync(engagementId),
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

        // Phase 3b: static reply for help/greeting — no LLM needed
        if (toolName is null)
        {
            var q = question.ToLowerInvariant().Trim();
            bool isHelp = (
                q.Contains("help") || q.Contains("what can") || q.Contains("hi") ||
                q.Contains("hello") || q.Contains("test") || q.Contains("capabilit") ||
                q.Contains("what do you") || q.Contains("what are you") ||
                q.Contains("what can you") || q.Contains("think about") ||
                q.Contains("opinion") || q.Contains("what should i") ||
                q.Contains("advice") || q.Contains("thoughts") ||
                q.Contains("feel about") || q.Contains("how do you") ||
                q.Contains("how do u") || q.Contains("good idea") ||
                q.Contains("bad idea") || q.Contains("worried") ||
                (q.Length < 20 && q.Contains("?")));

            if (isHelp)
            {
                bool isOpinion = q.Contains("think about") || q.Contains("opinion") ||
                    q.Contains("thoughts") || q.Contains("what should");

                string reply = isOpinion
                    ? "I am a read-only data advisor - I can report on your migration data but I do not form opinions or make judgment calls. " +
                      "Try asking me something specific like:\n\n" +
                      "* 'What percentage of my vault has migrated?'\n" +
                      "* 'Am I ready to migrate?' (checks prerequisites)\n" +
                      "* 'Scan my environment for risks'\n" +
                      "* 'What failed in my last run?'"
                    : "Here is what I can help with:\n\n" +
                      "* **Check prerequisites** - verify connections, inventory, platform auth, OAuth2\n" +
                      "* **Migration stats** - percentage migrated, counts by type, job dates\n" +
                      "* **Environment summary** - vault size, managed accounts, migration approach\n" +
                      "* **Reconciliation status** - matched vs unmatched items source vs target\n" +
                      "* **Risk scan** - large files, duplicates, managed account risks\n" +
                      "* **Explain failures** - what failed in the last run and how to fix it\n" +
                      "* **Recent activity** - event log entries\n\n" +
                      "Just ask naturally - for example: 'what failed?', 'am I ready to migrate?', 'show my progress'";

                yield return Token(reply);
                yield return Done();
                yield break;
            }
        }

        // Phase 3b: LLM narration — for open-ended questions and environment_summary
        yield return Phase("safety_check", "Checking input safety...");
        var promptVerdict = await guard.CheckPromptAsync(question, ct);
        if (!promptVerdict.Safe)
        {
            await LogSafetyEventAsync(engagementId,
                "unsafe input - blocked", promptVerdict.Category, question, ct);
            yield return Token(
                "I can't help with that request — it was flagged by the content safety filter. " +
                "If you believe this is a mistake, contact your administrator.");
            yield return Done();
            yield break;
        }

        yield return Phase("generating", "Generating response (CPU model)...");


        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, AssistantPrompt.System)
        };

        if (history is { Count: > 0 })
        {
            foreach (var t in history.TakeLast(4))
            {
                messages.Add(new(ChatRole.User, t.Question));
                // Direct-card turns have a synthetic summary from the frontend
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

        var fullAnswer = new System.Text.StringBuilder();
        await foreach (var token in ollama.ChatStreamAsync(messages, ct))
        {
            if (ct.IsCancellationRequested) yield break;
            fullAnswer.Append(token);
            yield return Token(token);
        }

        // Output-side check runs AFTER the full answer has already streamed to the browser —
        // by design (see ContentGuard's doc comment): this is a background safety-net audit for
        // review, not a pre-display gate. A pre-display gate would require buffering the entire
        // answer before showing any of it, which trades away the live-typing streaming UX this
        // was built around. This still has to be awaited rather than fired-and-forgotten, though:
        // AssistantService is request-scoped, and its IDbConnection would be disposed once the
        // request ends, so an unawaited background write here could silently fail.
        var answerVerdict = await guard.CheckResponseAsync(question, fullAnswer.ToString(), ct);
        if (!answerVerdict.Safe)
            await LogSafetyEventAsync(engagementId,
                "unsafe output - flagged for review", answerVerdict.Category, fullAnswer.ToString(), ct);

        yield return Done();
    }

    /// <summary>
    /// Records a content-guard verdict to event_log (event_type "ai_safety") so flagged input/
    /// output shows up in the Logs page like any other event — searchable and filterable there
    /// rather than needing a separate review surface. Best-effort: a logging failure here must
    /// never break the assistant response itself.
    /// </summary>
    private async Task LogSafetyEventAsync(
        Guid engagementId, string outcome, string? category, string content, CancellationToken ct)
    {
        try
        {
            const int maxLen = 500;
            var snippet = content.Length > maxLen ? content[..maxLen] + "…" : content;
            await db.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO event_log (engagement_id, event_type, action, outcome, message)
                  VALUES (@Eng, 'ai_safety', 'content guard', @Outcome, @Message)",
                new
                {
                    Eng = engagementId,
                    Outcome = category is null ? outcome : $"{outcome} ({category})",
                    Message = snippet,
                },
                cancellationToken: ct));
        }
        catch (Exception ex)
        {
            log.LogError("Failed to record content-guard event: {Message}", ex.Message);
        }
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
