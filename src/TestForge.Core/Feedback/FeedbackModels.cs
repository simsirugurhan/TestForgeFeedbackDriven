namespace TestForge.Core.Feedback;

public sealed record BuildResult(bool Succeeded, IReadOnlyList<string> Errors, string RawOutput)
{
    public static BuildResult Success(string rawOutput = "") => new(true, [], rawOutput);
    public static BuildResult Failure(IReadOnlyList<string> errors, string rawOutput = "") => new(false, errors, rawOutput);
}

/// <summary>Line coverage of the SOURCE file under test (not of the test files).</summary>
public sealed record CoverageReport(
    string SourceFile,
    double LinePercent,
    IReadOnlyList<int> CoveredLines,
    IReadOnlyList<int> MissingLines);

public sealed record TestRunResult(
    int Passed,
    int Failed,
    int Skipped,
    bool BuildSucceeded,
    IReadOnlyList<string> CompileErrors,
    string RawOutput,
    CoverageReport? Coverage)
{
    public int Total => Passed + Failed + Skipped;

    /// <summary>Finish acceptance rule: builds, nothing red, at least one real pass.</summary>
    public bool IsGreen => BuildSucceeded && Failed == 0 && Passed > 0;

    public static TestRunResult BuildFailure(IReadOnlyList<string> errors, string rawOutput) =>
        new(0, 0, 0, false, errors, rawOutput, null);
}

/// <summary>Reserved for v2 (mutation testing). No v1 adapter produces it.</summary>
public sealed record MutationReport(double MutationScore, int Killed, int Survived, int NoCoverage);
