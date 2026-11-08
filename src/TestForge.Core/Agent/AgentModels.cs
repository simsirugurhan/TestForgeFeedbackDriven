using TestForge.Core.Feedback;
using TestForge.Core.Llm;

namespace TestForge.Core.Agent;

public enum StopReason
{
    Finish,
    Stagnation,
    MaxIterations,
    Error,
}

public sealed record ToolCallRecord(string Name, string? Path = null);

/// <summary>Snapshot of one test execution (from run_tests or finish validation).</summary>
public sealed record TestSnapshot(int Passed, int Failed, double? CoveragePercent, bool BuildSucceeded)
{
    public static TestSnapshot From(TestRunResult run) =>
        new(run.Passed, run.Failed, run.Coverage?.LinePercent, run.BuildSucceeded);

    /// <summary>The (failed, coverage%) pair used by stagnation detection.</summary>
    public (int Failed, double? Coverage) ProgressKey => (Failed, CoveragePercent is { } c ? Math.Round(c, 2) : null);
}

/// <summary>One iteration = one LLM call plus all tool calls it requested.</summary>
public sealed class IterationRecord
{
    public required int Index { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public List<ToolCallRecord> ToolCalls { get; } = [];
    public List<TestSnapshot> TestRuns { get; } = [];

    /// <summary>run_tests was called (finish validation does not count).</summary>
    public bool HasRunTests { get; set; }

    /// <summary>A write_file or edit_file call actually changed a file.</summary>
    public bool HasEditOrWrite { get; set; }

    public bool ReflectionAdded { get; set; }
    public bool FinishRejected { get; set; }
    public LlmUsage Usage { get; set; } = LlmUsage.Zero;

    public TestSnapshot? LastTestRun => TestRuns.Count > 0 ? TestRuns[^1] : null;
}

public sealed record AgentOutcome(
    StopReason StopReason,
    string StopDetail,
    string? FinishReason,
    IReadOnlyList<IterationRecord> Iterations,
    LlmUsage Usage);
