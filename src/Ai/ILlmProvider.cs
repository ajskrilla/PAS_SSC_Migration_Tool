namespace PasMigration.Ai;

/// <summary>
/// Provider abstraction for the AI layer. Ollama (local, default) and Azure OpenAI both
/// implement this. Ollama's OpenAI-compatible endpoint keeps the abstraction cheap.
///
/// Security: implementations must never receive credentials, tokens, or secret values -
/// only metadata/analytics passed via explicit read-only tools. The AI is advisory and
/// read-only; it never executes migrations or writes to a tenant.
/// </summary>
public interface ILlmProvider
{
    string Name { get; }

    Task<ChatResult> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken ct = default);

    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}

public enum ChatRole { System, User, Assistant, Tool }

public sealed record ChatMessage(ChatRole Role, string Content, string? ToolCallId = null);

public sealed record ToolDefinition(string Name, string Description, string JsonSchema);

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

public sealed record ChatResult(string Content, IReadOnlyList<ToolCall> ToolCalls);
