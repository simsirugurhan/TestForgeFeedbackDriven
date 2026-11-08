using System.Text.Json;
using TestForge.Cli;
using TestForge.Core.Agent;
using TestForge.Core.Llm;
using TestForge.Core.Reporting;
using TestForge.Tests.Fakes;

namespace TestForge.Tests;

public class ReportTests
{
    private static AgentOutcome SampleOutcome()
    {
        var it1 = new IterationRecord { Index = 1, Usage = new LlmUsage(100, 20, 120) };
        it1.ToolCalls.Add(new ToolCallRecord("write_file", "Lib.Tests/FooTests.cs"));
        var it2 = new IterationRecord { Index = 2, Usage = new LlmUsage(200, 30, 230), HasRunTests = true, ReflectionAdded = true };
        it2.ToolCalls.Add(new ToolCallRecord("run_tests"));
        it2.TestRuns.Add(new TestSnapshot(5, 1, 65, true));
        var it3 = new IterationRecord { Index = 3, Usage = new LlmUsage(300, 40, 340) };
        it3.ToolCalls.Add(new ToolCallRecord("edit_file", "Lib.Tests/FooTests.cs"));
        var it4 = new IterationRecord { Index = 4, Usage = new LlmUsage(300, 40, 340), HasRunTests = true };
        it4.ToolCalls.Add(new ToolCallRecord("run_tests"));
        it4.TestRuns.Add(new TestSnapshot(8, 0, 82, true));
        return new AgentOutcome(StopReason.Finish, "finish", "all green", [it1, it2, it3, it4], new LlmUsage(900, 130, 1030));
    }

    private static RunReport SampleReport() => RunReportWriter.Build(
        new ReportMeta(DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow, "C:/repo", "Lib/Foo.cs", "Lib.Tests/Lib.Tests.csproj", "csharp", "gpt-4o", 25),
        SampleOutcome(),
        FakeLanguageAdapter.Green(9, 100));

    [Fact]
    public void Json_contains_every_schema_field()
    {
        using var doc = JsonDocument.Parse(RunReportWriter.ToJson(SampleReport()));
        var root = doc.RootElement;
        foreach (var key in new[] { "startedAt", "finishedAt", "repoRoot", "sourceFile", "testProject", "adapter", "model", "maxIterations" })
            Assert.True(root.GetProperty("meta").TryGetProperty(key, out _), $"meta.{key}");
        foreach (var key in new[] { "stopReason", "finishReason", "finalPassed", "finalFailed", "finalCoveragePercent" })
            Assert.True(root.GetProperty("outcome").TryGetProperty(key, out _), $"outcome.{key}");
        foreach (var key in new[] { "promptTokens", "completionTokens", "totalTokens" })
            Assert.True(root.GetProperty("usage").TryGetProperty(key, out _), $"usage.{key}");
        Assert.Equal(JsonValueKind.Null, root.GetProperty("mutation").ValueKind);
        Assert.Equal("finish", root.GetProperty("outcome").GetProperty("stopReason").GetString());

        var iterations = root.GetProperty("iterations");
        Assert.True(iterations.GetArrayLength() > 0);
        foreach (var it in iterations.EnumerateArray())
        {
            foreach (var key in new[] { "index", "passed", "failed", "coveragePercent", "tokensUsedThisIteration", "cumulativeTokens", "toolCallsCount", "toolCalls", "reflectionAdded", "timestamp" })
                Assert.True(it.TryGetProperty(key, out _), $"iterations[].{key}");
            Assert.True(it.GetProperty("toolCalls").GetArrayLength() > 0);
        }
    }

    [Fact]
    public void Tool_calls_carry_a_path_only_for_file_writes()
    {
        using var doc = JsonDocument.Parse(RunReportWriter.ToJson(SampleReport()));
        var calls = doc.RootElement.GetProperty("iterations").EnumerateArray()
            .SelectMany(i => i.GetProperty("toolCalls").EnumerateArray()).ToList();
        var edit = calls.First(c => c.GetProperty("name").GetString() == "edit_file");
        Assert.Equal("Lib.Tests/FooTests.cs", edit.GetProperty("path").GetString());
        var run = calls.First(c => c.GetProperty("name").GetString() == "run_tests");
        Assert.False(run.TryGetProperty("path", out _));
    }

    [Fact]
    public void Iterations_without_a_test_run_carry_the_last_known_values()
    {
        var report = SampleReport();
        Assert.Equal(0, report.Iterations[0].CoveragePercent);
        Assert.Equal(65, report.Iterations[2].CoveragePercent);
        Assert.Equal(1, report.Iterations[2].Failed);
        Assert.Equal(1030, report.Iterations[^1].CumulativeTokens);
        Assert.Equal(100, report.Outcome.FinalCoveragePercent); // from the orchestrator's final run
    }

    [Fact]
    public void Markdown_has_summary_table_and_coverage_trend()
    {
        var md = RunReportWriter.ToMarkdown(SampleReport());
        Assert.Contains("| # | passed | failed | coverage% | tokens | toolCalls |", md);
        Assert.Contains("%65 → %82", md);
        Assert.Contains("edit_file(FooTests.cs)", md);
        Assert.Contains("all green", md);
    }
}

public class CliArgumentsTests
{
    [Fact]
    public void Parses_generate_with_optional_test_project()
    {
        var a = CliArguments.Parse(["generate", "--repo", "samples", "--file", "X/Y.cs", "--max-iter", "10"]);
        Assert.Equal(("samples", "X/Y.cs", (string?)null, 10), (a.Repo, a.File, a.TestProject, a.MaxIterations));
    }

    [Fact]
    public void Mutation_flag_is_rejected_in_v1()
    {
        var ex = Assert.Throws<ArgumentException>(() => CliArguments.Parse(["generate", "--repo", "r", "--file", "f", "--mutation"]));
        Assert.Contains("v1", ex.Message);
    }

    [Theory]
    [InlineData("generate", "--file", "f")]
    [InlineData("generate", "--repo", "r")]
    [InlineData("deploy")]
    public void Missing_or_unknown_arguments_are_errors(params string[] args) =>
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(args));
}
