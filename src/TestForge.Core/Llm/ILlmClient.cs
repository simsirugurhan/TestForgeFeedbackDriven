namespace TestForge.Core.Llm;

/// <summary>
/// Provider-neutral LLM client. The agent depends only on this interface, so tests can
/// drive it deterministically and no Azure/OpenAI SDK types leak into the core.
/// </summary>
public interface ILlmClient
{
    /// <summary>Model or deployment name, written to the run report.</summary>
    string ModelName { get; }

    Task<LlmResponse> GetCompletionAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition> tools,
        CancellationToken ct);
}

public static class LlmRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

public sealed record LlmMessage(
    string Role,
    string? Content,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? ToolCallId = null)
{
    public static LlmMessage System(string content) => new(LlmRoles.System, content);
    public static LlmMessage User(string content) => new(LlmRoles.User, content);
    public static LlmMessage Assistant(string? content, IReadOnlyList<LlmToolCall>? toolCalls) =>
        new(LlmRoles.Assistant, content, toolCalls is { Count: > 0 } ? toolCalls : null);
    public static LlmMessage ToolResult(string toolCallId, string content) =>
        new(LlmRoles.Tool, content, null, toolCallId);

    /// <summary>Deep copy, so recorded conversations cannot be mutated afterwards.</summary>
    public LlmMessage DeepCopy() => this with { ToolCalls = ToolCalls?.Select(t => t with { }).ToList() };
}

public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

public sealed record LlmToolDefinition(string Name, string Description, string ParametersJsonSchema);

public sealed record LlmResponse(string? Content, IReadOnlyList<LlmToolCall> ToolCalls, LlmUsage Usage);

public sealed record LlmUsage(int PromptTokens, int CompletionTokens, int TotalTokens)
{
    public static LlmUsage Zero { get; } = new(0, 0, 0);

    public LlmUsage Add(LlmUsage other) => new(
        PromptTokens + other.PromptTokens,
        CompletionTokens + other.CompletionTokens,
        TotalTokens + other.TotalTokens);
}
