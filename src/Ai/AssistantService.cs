using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;

namespace PasMigration.Ai;

// ── Catalog + Router (unchanged from before) ──────────────────────────────────────────

public sealed record ToolEntry(string Name, string Description);

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

public sealed class AssistantRouter(ILlmProvider provider, AssistantCatalog catalog, ILogger<AssistantRouter> log)
{
    private const float Threshold = 0.50f; // slightly lower than before — improves recall

    public async Task<string?> RouteAsync(string question, CancellationToken ct)
    {
        var qEmb  = await provider.EmbedAsync(question, ct);
        var cEmbs = await catalog.GetEmbeddingsAsync(ct);
        float best = float.MinValue; int bestIdx = -1;
        for (int i = 0; i < cEmbs.Length; i++)
        {
            float sim = Cosine(qEmb, cEmbs[i]);
            if (sim > best) { best = sim; bestIdx = i; }
        }
        var name = bestIdx >= 0 ? AssistantCatalog.Entries[bestIdx].Name : null;
        log.LogDebug("Router: tool={Tool} score={Score:F3}", name, best);
        return best >= Threshold ? name : null;
    }

    private static float Cosine(float[] a, float[] b)
    {
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i]*b[i]; na += a[i]*a[i]; nb += b[i]*b[i]; }
        float d = MathF.Sqrt(na) * MathF.Sqrt(nb);
        return d < 1e-8f ? 0f : dot / d;
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
    /// Streams the assistant response as SSE lines:
    ///   data: {"type":"tool","tool":"migration_stats"}
    ///   data: {"type":"token","text":"Here is what I found…"}
    ///   data: {"type":"done"}
    ///   data: {"type":"error","message":"…"}
    /// </summary>
    public async IAsyncEnumerable<string> AskStreamAsync(
        Guid engagementId,
        string question,
        IReadOnlyList<ConversationTurn>? history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // 1. Route
        string? toolName = null;
        try { toolName = await router.RouteAsync(question, ct); }
        catch (Exception ex)
        {
            log.LogWarning("Router failed: {Message}", ex.Message);
        }

        if (toolName is not null)
            yield return Sse("tool", new { tool = toolName });

        // 2. Run tool
        object? toolResult = null;
        if (toolName is not null)
        {
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
                // Cannot yield inside catch — store error and fall through
                toolResult = new { error = $"Tool failed: {ex.Message}" };
            }
        }

        // 3. Build messages — keep them lean for CPU inference
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, AssistantPrompt.System)
        };

        // Last 4 turns only — reduces prompt size significantly on CPU
        if (history is { Count: > 0 })
        {
            foreach (var t in history.TakeLast(4))
            {
                messages.Add(new(ChatRole.User,      t.Question));
                messages.Add(new(ChatRole.Assistant, t.Answer));
            }
        }

        string userMessage = toolResult is not null
            ? $"""
               User question: {question}

               Tool: {toolName}
               Data:
               {JsonSerializer.Serialize(toolResult, new JsonSerializerOptions { WriteIndented = true })}

               Narrate this data in plain language. Be concise. Use bullet points.
               Do not invent anything not in the data above.
               """
            : $"""
               User question: {question}

               Answer from your knowledge of the PAS → Secret Server migration tool and methodology.
               If the user says "help", explain your five capabilities briefly.
               """;

        messages.Add(new(ChatRole.User, userMessage));

        // 4. Stream tokens
        await foreach (var token in ollama.ChatStreamAsync(messages, ct))
        {
            if (ct.IsCancellationRequested) yield break;
            yield return Sse("token", new { text = token });
        }

        yield return Sse("done", new { });
    }

    private static string Sse(string type, object payload) =>
        $"data: {JsonSerializer.Serialize(new Dictionary<string, object> { ["type"] = type }
            .Concat(JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(payload)) ?? [])
            .ToDictionary(k => k.Key, v => v.Value))}\n\n";
}

// ── DTOs ──────────────────────────────────────────────────────────────────────────────

public sealed record ConversationTurn(string Question, string Answer);
public sealed record AssistantRequest(string Question, IReadOnlyList<ConversationTurn>? History);
