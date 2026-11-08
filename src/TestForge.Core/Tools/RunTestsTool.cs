using System.Globalization;
using System.Text;
using TestForge.Core.Feedback;
using TestForge.Core.Workspace;

namespace TestForge.Core.Tools;

/// <summary>The only way to execute tests. Returns execution AND coverage feedback in one call.</summary>
public sealed class RunTestsTool : ITool
{
    public string Name => "run_tests";

    public string Description =>
        "Build and run the test project, then report passed/failed/skipped tests, compile errors, " +
        "full failure output and line coverage of the source file under test (including EVERY uncovered line).";

    public string ParametersJsonSchema => """{ "type": "object", "properties": {} }""";

    public async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext context, CancellationToken ct)
    {
        var run = await context.Adapter.RunTestsAsync(context.Sandbox.RepoRoot, context.TestProject, context.SourceFile, ct);
        var text = TestRunFormatter.Format(run, context.Sandbox);
        return (run.BuildSucceeded ? ToolResult.Ok(text) : ToolResult.Error(text)) with { TestRun = run };
    }
}

public static class TestRunFormatter
{
    public static string Format(TestRunResult run, Sandbox sandbox, string header = "[run_tests]")
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{header} build: {(run.BuildSucceeded ? "OK" : "FAILED")} | passed: {run.Passed} | failed: {run.Failed} | skipped: {run.Skipped}");

        if (!run.BuildSucceeded)
        {
            sb.AppendLine($"[compile errors] {run.CompileErrors.Count}");
            foreach (var e in run.CompileErrors.Take(40)) sb.AppendLine("  " + e);
        }

        if (run.Coverage is { } cov)
        {
            sb.AppendLine($"[coverage] {sandbox.ToRelative(cov.SourceFile)}: %{cov.LinePercent.ToString("0.0", CultureInfo.InvariantCulture)} line coverage " +
                          $"({cov.CoveredLines.Count}/{cov.CoveredLines.Count + cov.MissingLines.Count} satır)");
            sb.AppendLine($"missing lines: {(cov.MissingLines.Count == 0 ? "yok" : FormatRanges(cov.MissingLines))}");
        }
        else if (run.BuildSucceeded)
        {
            sb.AppendLine("[coverage] hedef kaynak dosya için coverage verisi yok (test projesi kaynak projeye referans veriyor mu?).");
        }

        if (!string.IsNullOrWhiteSpace(run.RawOutput))
        {
            sb.AppendLine("[output]");
            sb.AppendLine(OutputTrimmer.Trim(run.RawOutput));
        }
        return sb.ToString();
    }

    /// <summary>Compresses line numbers into ranges without dropping any line: "3, 7-9, 12".</summary>
    public static string FormatRanges(IEnumerable<int> lines)
    {
        var sorted = lines.Distinct().Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var start = sorted[i];
            while (i + 1 < sorted.Count && sorted[i + 1] == sorted[i] + 1) i++;
            parts.Add(start == sorted[i] ? $"{start}" : $"{start}-{sorted[i]}");
        }
        return string.Join(", ", parts);
    }
}
