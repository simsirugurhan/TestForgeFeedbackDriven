using System.Text.Json;
using TestForge.Core.Adapters;
using TestForge.Core.Feedback;
using TestForge.Core.Workspace;

namespace TestForge.Core.Tools;

/// <summary>Everything a tool may touch during one run.</summary>
public sealed record ToolContext(
    Sandbox Sandbox,
    ILanguageAdapter Adapter,
    string SourceFile,
    string TestProject);

public sealed record FinishDecision(bool Accepted, string Reason);

public sealed record ToolResult(bool Success, string Output)
{
    /// <summary>Repo-relative path written by write_file/edit_file (for the report).</summary>
    public string? WrittenPath { get; init; }

    /// <summary>edit_file only: the edit applied but the follow-up build failed.</summary>
    public bool HasBuildError { get; init; }

    /// <summary>Test run executed by run_tests or by finish validation.</summary>
    public TestRunResult? TestRun { get; init; }

    public FinishDecision? Finish { get; init; }

    public static ToolResult Ok(string output) => new(true, output);
    public static ToolResult Error(string output) => new(false, output);
}

public interface ITool
{
    string Name { get; }
    string Description { get; }
    string ParametersJsonSchema { get; }
    Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext context, CancellationToken ct);
}

public sealed class ToolArgumentException(string message) : ArgumentException(message);

/// <summary>Thin typed accessor over the JSON arguments the model produced.</summary>
public sealed class ToolArgs
{
    private readonly JsonElement _root;

    private ToolArgs(JsonElement root) => _root = root;

    public static ToolArgs Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ToolArgs(JsonDocument.Parse("{}").RootElement.Clone());
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ToolArgumentException("Araç argümanları bir JSON nesnesi olmalı.");
            return new ToolArgs(doc.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            throw new ToolArgumentException($"Araç argümanları geçerli JSON değil: {ex.Message}");
        }
    }

    public static ToolArgs FromObject(object value) => Parse(JsonSerializer.Serialize(value));

    public string? GetString(string name) =>
        _root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())
            : null;

    public string Require(string name) =>
        GetString(name) is { } s ? s : throw new ToolArgumentException($"'{name}' parametresi zorunlu.");

    public int? GetInt(string name)
    {
        if (!_root.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var j)) return j;
        return null;
    }
}
