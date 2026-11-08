using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TestForge.Core.Agent;
using TestForge.Core.Feedback;

namespace TestForge.Core.Reporting;

public static class RunReportWriter
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToStopReasonString(StopReason reason) => reason switch
    {
        StopReason.Finish => "finish",
        StopReason.Stagnation => "stagnation",
        StopReason.MaxIterations => "max_iterations",
        _ => "error",
    };

    /// <summary>
    /// Builds the report. Iterations without a test run carry the last known passed/failed/coverage values,
    /// so the coverage curve in the report is continuous.
    /// </summary>
    public static RunReport Build(ReportMeta meta, AgentOutcome outcome, TestRunResult? finalRun)
    {
        var iterations = new List<ReportIteration>();
        int passed = 0, failed = 0, cumulative = 0;
        double coverage = 0;
        foreach (var it in outcome.Iterations)
        {
            if (it.LastTestRun is { } last)
            {
                passed = last.Passed;
                failed = last.Failed;
                coverage = last.CoveragePercent ?? coverage;
            }
            cumulative += it.Usage.TotalTokens;
            iterations.Add(new ReportIteration(
                it.Index, passed, failed, Math.Round(coverage, 2),
                it.Usage.TotalTokens, cumulative, it.ToolCalls.Count,
                it.ToolCalls.Select(c => new ReportToolCall(c.Name, c.Path)).ToList(),
                it.ReflectionAdded, it.Timestamp));
        }

        var report = new ReportOutcome(
            ToStopReasonString(outcome.StopReason),
            outcome.StopDetail,
            outcome.FinishReason,
            finalRun?.Passed ?? passed,
            finalRun?.Failed ?? failed,
            Math.Round(finalRun?.Coverage?.LinePercent ?? coverage, 2),
            finalRun?.BuildSucceeded ?? false);

        return new RunReport(meta, report, iterations,
            new ReportUsage(outcome.Usage.PromptTokens, outcome.Usage.CompletionTokens, outcome.Usage.TotalTokens));
    }

    public static string ToJson(RunReport report) => JsonSerializer.Serialize(report, JsonOptions);

    public static string ToMarkdown(RunReport report)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"# TestForge raporu — {report.Meta.SourceFile}");
        sb.AppendLine();
        sb.AppendLine($"- **Repo:** `{report.Meta.RepoRoot}`");
        sb.AppendLine($"- **Kaynak dosya:** `{report.Meta.SourceFile}`");
        sb.AppendLine($"- **Test projesi:** `{report.Meta.TestProject}`");
        sb.AppendLine($"- **Adapter:** {report.Meta.Adapter} · **Model:** {report.Meta.Model}");
        sb.AppendLine($"- **Süre:** {(report.Meta.FinishedAt - report.Meta.StartedAt).TotalSeconds.ToString("0", inv)} sn");
        sb.AppendLine();
        sb.AppendLine("## Özet");
        sb.AppendLine();
        sb.AppendLine($"| Durdurma sebebi | Passed | Failed | Coverage | Toplam token |");
        sb.AppendLine($"|---|---|---|---|---|");
        sb.AppendLine($"| {report.Outcome.StopReason} | {report.Outcome.FinalPassed} | {report.Outcome.FinalFailed} | " +
                      $"%{report.Outcome.FinalCoveragePercent.ToString("0.0", inv)} | {report.Usage.TotalTokens} |");
        sb.AppendLine();
        if (!report.Outcome.FinalBuildSucceeded) sb.AppendLine("> ⚠️ Son koşuda test projesi derlenmedi.\n");
        sb.AppendLine($"Detay: {report.Outcome.StopDetail}");
        sb.AppendLine();
        sb.AppendLine("## Coverage gidişi");
        sb.AppendLine();
        sb.AppendLine(CoverageTrend(report));
        sb.AppendLine();
        sb.AppendLine("## İterasyonlar");
        sb.AppendLine();
        sb.AppendLine("| # | passed | failed | coverage% | tokens | toolCalls |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var it in report.Iterations)
        {
            var calls = string.Join(", ", it.ToolCalls.Select(c => c.Path is null ? c.Name : $"{c.Name}({Path.GetFileName(c.Path)})"));
            sb.AppendLine($"| {it.Index} | {it.Passed} | {it.Failed} | {it.CoveragePercent.ToString("0.0", inv)} | " +
                          $"{it.TokensUsedThisIteration} | {(calls.Length == 0 ? "—" : calls)} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Ajanın bitiş gerekçesi");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(report.Outcome.FinishReason) ? "_finish kabul edilmedi._" : report.Outcome.FinishReason);
        sb.AppendLine();
        sb.AppendLine("Mutation testing: v1'de atlandı (`mutation: null`).");
        return sb.ToString();
    }

    /// <summary>"%65 → %82 → %100": one value per change in coverage.</summary>
    public static string CoverageTrend(RunReport report)
    {
        var inv = CultureInfo.InvariantCulture;
        var values = new List<double>();
        // Leading zeros are iterations before the first test run (e.g. the zero-shot write).
        foreach (var c in report.Iterations.Select(i => i.CoveragePercent).SkipWhile(c => c == 0))
            if (values.Count == 0 || Math.Abs(values[^1] - c) > 0.001) values.Add(c);
        if (values.Count == 0) return "_coverage ölçülmedi._";
        return string.Join(" → ", values.Select(v => "%" + v.ToString("0.#", inv)));
    }

    public static async Task WriteAsync(RunReport report, string outputDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "report.json"), ToJson(report), ct);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "report.md"), ToMarkdown(report), ct);
    }
}
