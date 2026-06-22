using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PasMigration.Ai;

// ── Tool catalog (descriptions used for embedding-based routing) ──────────────────────

public sealed record ToolEntry(string Name, string Description);

/// <summary>
/// Singleton cache of catalog embeddings. Computed once on first request,
/// reused for the lifetime of the process.
/// </summary>
public sealed class AssistantCatalog(ILlmProvider provider, ILogger<AssistantCatalog> log)
{
    public static readonly ToolEntry[] Entries =
    [
        new("check_prerequisites",
            "Check whether the migration prerequisites are met: connections tested, inventory captured, platform unified, UVA mode, admin roles, OAuth2 app"),
        new("migration_stats",
            "Show migration progress statistics: how many secrets and accounts have been migrated, percentage complete, migration job history and dates"),
        new("environment_summary",
            "Summarise the source environment size: total secrets, accounts, managed account count, and recommend a migration approach or path"),
        new("reconciliation_status",
            "Show reconciliation results: which items are matched, source-only, target-only, or in conflict between source and target"),
        new("recent_activity",
            "Show recent activity and event log: what actions have been taken, outcomes, errors in this engagement"),
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
            log.LogInformation("Computing catalog embeddings ({Count} tools)…", Entries.Length);
            var embeddings = new float[Entries.Length][];
            for (int i = 0; i < Entries.Length; i++)
                embeddings[i] = await provider.EmbedAsync(Entries[i].Description, ct);
            _cache = embeddings;
            return _cache;
        }
        finally { _lock.Release(); }
    }
}

// ── Intent router ──────────────────────────────────────────────────────────────────────

public sealed class AssistantRouter(ILlmProvider provider, AssistantCatalog catalog, ILogger<AssistantRouter> log)
{
    private const float ConfidenceThreshold = 0.55f;

    /// <summary>
    /// Returns the best-matching tool name, or null if confidence is below threshold.
    /// </summary>
    public async Task<string?> RouteAsync(string question, CancellationToken ct)
    {
        var questionEmb = await provider.EmbedAsync(question, ct);
        var catalogEmbs = await catalog.GetEmbeddingsAsync(ct);

        float best = float.MinValue;
        int bestIdx = -1;

        for (int i = 0; i < catalogEmbs.Length; i++)
        {
            float sim = Cosine(questionEmb, catalogEmbs[i]);
            if (sim > best) { best = sim; bestIdx = i; }
        }

        var toolName = bestIdx >= 0 ? AssistantCatalog.Entries[bestIdx].Name : null;
        log.LogDebug("Router: best={Tool} score={Score:F3}", toolName, best);

        return best >= ConfidenceThreshold ? toolName : null;
    }

    private static float Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        float denom = MathF.Sqrt(na) * MathF.Sqrt(nb);
        return denom < 1e-8f ? 0f : dot / denom;
    }
}

// ── AssistantService (orchestrator) ───────────────────────────────────────────────────

public sealed class AssistantService(
    ILlmProvider provider,
    AssistantRouter router,
    IDbConnection db,
    ILogger<AssistantService> log)
{
    public async Task<AssistantReply> AskAsync(
        Guid engagementId,
        string question,
        IReadOnlyList<ConversationTurn>? history,
        CancellationToken ct)
    {
        // 1. Route the question to a tool (or fall through to help/general)
        var toolName = await router.RouteAsync(question, ct);
        log.LogInformation("Assistant: tool={Tool} engagement={Id}", toolName ?? "none", engagementId);

        // 2. Run the tool and get structured data
        object? toolResult = null;
        if (toolName is not null)
        {
            var tools = new AssistantTools(db);
            toolResult = toolName switch
            {
                "check_prerequisites"   => await tools.CheckPrerequisitesAsync(engagementId),
                "migration_stats"       => await tools.MigrationStatsAsync(engagementId),
                "environment_summary"   => await tools.EnvironmentSummaryAsync(engagementId),
                "reconciliation_status" => await tools.ReconciliationStatusAsync(engagementId),
                "recent_activity"       => await tools.RecentActivityAsync(engagementId),
                _ => null
            };
        }

        // 3. Build the message list for the model
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, AssistantPrompt.System)
        };

        // Include recent conversation turns for context (last 6)
        if (history is { Count: > 0 })
        {
            foreach (var turn in history.TakeLast(6))
            {
                messages.Add(new(ChatRole.User,      turn.Question));
                messages.Add(new(ChatRole.Assistant, turn.Answer));
            }
        }

        // Build the user message — include tool data if we have it
        string userMessage;
        if (toolResult is not null)
        {
            var json = JsonSerializer.Serialize(toolResult, new JsonSerializerOptions { WriteIndented = true });
            userMessage = $"""
                User question: {question}

                Tool used: {toolName}
                Tool data (read-only, from the migration database):
                {json}

                Please narrate this data in plain language, interpreting what it means for the migration.
                Be concise. Use bullet points where helpful. Do not invent any information not present above.
                """;
        }
        else
        {
            userMessage = $"""
                User question: {question}

                No specific tool was matched. Answer from general knowledge about the PAS migration tool
                and methodology. If the user is asking "help" or "what can you do", explain the five
                capabilities: check prerequisites, migration statistics, environment summary and migration
                path recommendation, reconciliation status, recent activity.
                """;
        }

        messages.Add(new(ChatRole.User, userMessage));

        // 4. Call the model
        var result = await provider.ChatAsync(messages, null, ct);

        return new AssistantReply(result.Content, toolName);
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────────────

public sealed record ConversationTurn(string Question, string Answer);
public sealed record AssistantReply(string Answer, string? ToolUsed);
public sealed record AssistantRequest(string Question, IReadOnlyList<ConversationTurn>? History);
