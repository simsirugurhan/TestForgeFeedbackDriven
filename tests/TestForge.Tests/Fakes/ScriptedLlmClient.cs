using TestForge.Core.Agent;
using TestForge.Core.Llm;

namespace TestForge.Tests.Fakes;

/// <summary>
/// Deterministic LLM: returns pre-scripted responses in order and records a deep copy of the
/// messages it received on every call, so tests can assert exactly what the agent sent.
/// </summary>
public sealed class ScriptedLlmClient(params LlmResponse[] responses) : ILlmClient
{
    private readonly List<IReadOnlyList<LlmMessage>> _calls = [];
    private int _next;

    public string ModelName => "scripted-fake";

    public IReadOnlyList<IReadOnlyList<LlmMessage>> Calls => _calls;
    public IReadOnlyList<LlmResponse> Responses { get; } = responses;

    public Task<LlmResponse> GetCompletionAsync(IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmToolDefinition> tools, CancellationToken ct)
    {
        _calls.Add(messages.Select(m => m.DeepCopy()).ToList());
        if (_next >= Responses.Count)
            throw new InvalidOperationException($"ScriptedLlmClient: beklenenden fazla çağrı ({_next + 1}. çağrı, {Responses.Count} yanıt tanımlı).");
        return Task.FromResult(Responses[_next++]);
    }

    // ---- assertion helpers -------------------------------------------------------------

    /// <summary>The last message the agent sent in call <paramref name="callIndex"/> is a reflection after <paramref name="toolName"/>.</summary>
    public void AssertReflectionAddedAfter(int callIndex, string toolName)
    {
        var messages = Calls[callIndex];
        var last = messages[^1];
        Assert.Equal(LlmRoles.User, last.Role);
        Assert.Contains(Prompts.ReflectionMarker, last.Content);

        var assistant = messages.Last(m => m.Role == LlmRoles.Assistant);
        Assert.Contains(assistant.ToolCalls!, c => c.Name == toolName);
    }

    /// <summary>No reflection was added after the previous assistant turn.</summary>
    public void AssertNoReflectionAfter(int callIndex)
    {
        var messages = Calls[callIndex];
        var lastAssistant = messages.ToList().FindLastIndex(m => m.Role == LlmRoles.Assistant);
        var after = messages.Skip(lastAssistant + 1);
        Assert.DoesNotContain(after, m => m.Content?.Contains(Prompts.ReflectionMarker) == true);
    }

    public void AssertToolResultContains(int callIndex, string substring)
    {
        var toolMessages = Calls[callIndex].Where(m => m.Role == LlmRoles.Tool).ToList();
        Assert.Contains(toolMessages, m => m.Content?.Contains(substring, StringComparison.Ordinal) == true);
    }

    public static string? LastUserMessage(IReadOnlyList<LlmMessage> call) =>
        call.LastOrDefault(m => m.Role == LlmRoles.User)?.Content;
}

/// <summary>Builders for scripted model responses.</summary>
public static class Respond
{
    private static int _id;

    public static LlmResponse Tools(params (string Name, object? Args)[] calls) => new(
        null,
        calls.Select(c => new LlmToolCall($"call_{Interlocked.Increment(ref _id)}", c.Name,
            System.Text.Json.JsonSerializer.Serialize(c.Args ?? new { }))).ToList(),
        new LlmUsage(100, 20, 120));

    public static LlmResponse Tool(string name, object? args = null) => Tools((name, args));

    public static LlmResponse Text(string text) => new(text, [], new LlmUsage(50, 10, 60));
}
