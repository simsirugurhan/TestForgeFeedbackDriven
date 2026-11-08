using System.Text.Json.Serialization;

namespace TestForge.Core.Reporting;

/// <summary>Schema v1 of .testforge/report.json. Property names are serialized in camelCase.</summary>
public sealed record RunReport(
    ReportMeta Meta,
    ReportOutcome Outcome,
    IReadOnlyList<ReportIteration> Iterations,
    ReportUsage Usage,
    object? Mutation = null);

public sealed record ReportMeta(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string RepoRoot,
    string SourceFile,
    string TestProject,
    string Adapter,
    string Model,
    int MaxIterations);

public sealed record ReportOutcome(
    string StopReason,
    string StopDetail,
    string? FinishReason,
    int FinalPassed,
    int FinalFailed,
    double FinalCoveragePercent,
    bool FinalBuildSucceeded);

public sealed record ReportIteration(
    int Index,
    int Passed,
    int Failed,
    double CoveragePercent,
    int TokensUsedThisIteration,
    int CumulativeTokens,
    int ToolCallsCount,
    IReadOnlyList<ReportToolCall> ToolCalls,
    bool ReflectionAdded,
    DateTimeOffset Timestamp);

public sealed record ReportToolCall(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null);

public sealed record ReportUsage(int PromptTokens, int CompletionTokens, int TotalTokens);
