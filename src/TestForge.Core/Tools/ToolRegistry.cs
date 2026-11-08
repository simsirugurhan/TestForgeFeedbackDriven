using TestForge.Core.Llm;

namespace TestForge.Core.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        foreach (var tool in tools) _tools.Add(tool.Name, tool);
    }

    /// <summary>
    /// The v1 tool set. There is deliberately no build, coverage or mutation tool:
    /// run_tests returns coverage, and edit_file builds automatically.
    /// </summary>
    public static ToolRegistry CreateDefault() => new(
    [
        new ViewFileTool(),
        new SearchFileTool(),
        new WriteFileTool(),
        new EditFileTool(),
        new ExecuteShellTool(),
        new RunTestsTool(),
        new FinishTool(),
    ]);

    public IReadOnlyCollection<string> Names => _tools.Keys;

    public IReadOnlyList<LlmToolDefinition> Definitions =>
        _tools.Values.Select(t => new LlmToolDefinition(t.Name, t.Description, t.ParametersJsonSchema)).ToList();

    public async Task<ToolResult> ExecuteAsync(LlmToolCall call, ToolContext context, CancellationToken ct)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
            return ToolResult.Error($"Bilinmeyen araç '{call.Name}'. Kullanılabilir araçlar: {string.Join(", ", _tools.Keys)}.");

        try
        {
            var args = ToolArgs.Parse(call.ArgumentsJson);
            return await tool.ExecuteAsync(args, context, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ToolArgumentException or UnauthorizedAccessException or IOException)
        {
            return ToolResult.Error($"[{call.Name}] hata: {ex.Message}");
        }
    }
}
