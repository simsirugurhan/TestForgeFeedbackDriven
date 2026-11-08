using TestForge.Core.Agent;
using TestForge.Core.Feedback;
using TestForge.Core.Llm;
using TestForge.Core.Tools;
using TestForge.Tests.Fakes;

namespace TestForge.Tests;

public abstract class AgentTestBase : IDisposable
{
    protected const string TestFile = "Lib.Tests/FooTests.cs";
    protected readonly TempRepo Repo = new();

    protected static object Write(string content = "class FooTests { /*v1*/ }") => new { path = TestFile, content };
    protected static object Edit(string from, string to) => new { path = TestFile, mode = "replace", old_text = from, new_text = to };

    protected async Task<(AgentOutcome Outcome, ScriptedLlmClient Llm)> Run(int maxIterations, params LlmResponse[] script)
    {
        var llm = new ScriptedLlmClient(script);
        var agent = new TestForgeAgent(llm, ToolRegistry.CreateDefault(), new AgentOptions { MaxIterations = maxIterations });
        var outcome = await agent.RunAsync(Repo.Context, "zero-shot prompt", CancellationToken.None);
        return (outcome, llm);
    }

    public void Dispose() => Repo.Dispose();
}

public class AgentHappyPathTests : AgentTestBase
{
    [Fact]
    public async Task Zero_shot_then_fail_then_fix_then_finish_is_accepted()
    {
        Repo.Adapter.TestRuns.Enqueue(FakeLanguageAdapter.Red(2, 1, 65)); // run_tests #1
        Repo.Adapter.TestRuns.Enqueue(FakeLanguageAdapter.Green(3, 100)); // run_tests #2
        Repo.Adapter.TestRuns.Enqueue(FakeLanguageAdapter.Green(3, 100)); // finish validation

        var (outcome, llm) = await Run(25,
            Respond.Tool("write_file", Write()),
            Respond.Tool("run_tests"),
            Respond.Tool("edit_file", Edit("v1", "v2")),
            Respond.Tool("run_tests"),
            Respond.Tool("finish", new { reason = "all green, 100% coverage" }));

        Assert.Equal(StopReason.Finish, outcome.StopReason);
        Assert.Equal("all green, 100% coverage", outcome.FinishReason);
        Assert.Equal(5, outcome.Iterations.Count);
        Assert.Equal(5, llm.Calls.Count);
        Assert.Contains("v2", Repo.Read(TestFile));
        Assert.Equal(600, outcome.Usage.TotalTokens);

        // First call carries the system prompt and the zero-shot prompt.
        Assert.Equal(LlmRoles.System, llm.Calls[0][0].Role);
        Assert.Equal("zero-shot prompt", llm.Calls[0][1].Content);
    }

    [Fact]
    public async Task Response_without_tool_calls_gets_a_nudge_and_counts_as_an_iteration()
    {
        var (outcome, llm) = await Run(2, Respond.Text("Here are some tests"), Respond.Tool("finish", new { reason = "done" }));
        Assert.Equal(StopReason.Finish, outcome.StopReason);
        Assert.Equal(Prompts.NoToolCallNudge, ScriptedLlmClient.LastUserMessage(llm.Calls[1]));
    }

    [Fact]
    public async Task Llm_failure_stops_with_error()
    {
        var (outcome, _) = await Run(5); // no scripted responses → fake throws
        Assert.Equal(StopReason.Error, outcome.StopReason);
        Assert.Contains("LLM", outcome.StopDetail);
    }
}

public class AgentReflectionTests : AgentTestBase
{
    [Fact]
    public async Task Reflection_is_added_after_run_tests()
    {
        var (_, llm) = await Run(2, Respond.Tool("run_tests"), Respond.Tool("finish", new { reason = "ok" }));
        llm.AssertReflectionAddedAfter(1, "run_tests");
    }

    [Fact]
    public async Task No_reflection_after_execute_bash()
    {
        var (_, llm) = await Run(2, Respond.Tool("execute_bash", new { command = "ls Lib" }), Respond.Tool("finish", new { reason = "ok" }));
        llm.AssertNoReflectionAfter(1);
    }

    [Fact]
    public async Task No_reflection_after_view_file()
    {
        var (_, llm) = await Run(2, Respond.Tool("view_file", new { path = "Lib/Foo.cs" }), Respond.Tool("finish", new { reason = "ok" }));
        llm.AssertNoReflectionAfter(1);
    }

    [Fact]
    public async Task No_reflection_after_successful_edit()
    {
        Repo.Write(TestFile, "class FooTests { /*v1*/ }");
        var (_, llm) = await Run(2, Respond.Tool("edit_file", Edit("v1", "v2")), Respond.Tool("finish", new { reason = "ok" }));
        llm.AssertNoReflectionAfter(1);
    }

    [Fact]
    public async Task Reflection_is_added_after_edit_with_build_error()
    {
        Repo.Write(TestFile, "class FooTests { /*v1*/ }");
        Repo.Adapter.Builds.Enqueue(BuildResult.Failure(["FooTests.cs(1,1): error CS1002: ; expected"]));
        var (_, llm) = await Run(2, Respond.Tool("edit_file", Edit("v1", "v2")), Respond.Tool("finish", new { reason = "ok" }));
        llm.AssertReflectionAddedAfter(1, "edit_file");
        llm.AssertToolResultContains(1, "CS1002");
    }

    [Fact]
    public async Task Finish_rejection_adds_the_rejection_text_and_a_reflection()
    {
        Repo.Adapter.TestRuns.Enqueue(FakeLanguageAdapter.Red(1, 2, 50));
        var (_, llm) = await Run(2, Respond.Tool("finish", new { reason = "give up" }), Respond.Tool("finish", new { reason = "now green" }));
        llm.AssertReflectionAddedAfter(1, "finish");
        Assert.Contains("finish reddedildi", ScriptedLlmClient.LastUserMessage(llm.Calls[1]));
        llm.AssertToolResultContains(1, "2 failed");
    }

    [Fact]
    public async Task One_reflection_per_iteration_even_with_several_tool_calls()
    {
        var (_, llm) = await Run(2,
            Respond.Tools(("run_tests", null), ("view_file", new { path = "Lib/Foo.cs" }), ("run_tests", null)),
            Respond.Tool("finish", new { reason = "ok" }));
        var reflections = llm.Calls[1].Count(m => m.Content?.Contains(Prompts.ReflectionMarker) == true);
        Assert.Equal(1, reflections);
        Assert.Equal(3, llm.Calls[1].Count(m => m.Role == LlmRoles.Tool));
    }
}

public class AgentFinishTests : AgentTestBase
{
    [Fact]
    public async Task Finish_with_red_tests_is_rejected_and_the_loop_continues()
    {
        Repo.Adapter.TestRuns.Enqueue(FakeLanguageAdapter.Red(2, 1, 70));
        var (outcome, llm) = await Run(5, Respond.Tool("finish", new { reason = "x" }), Respond.Tool("finish", new { reason = "y" }));
        Assert.Equal(StopReason.Finish, outcome.StopReason);
        Assert.Equal("y", outcome.FinishReason);
        Assert.Equal(2, outcome.Iterations.Count); // the rejection consumed an iteration
        Assert.True(outcome.Iterations[0].FinishRejected);
        llm.AssertToolResultContains(1, "testler hâlâ kırmızı (1 failed");
    }

    [Fact]
    public async Task Finish_with_compile_errors_is_rejected()
    {
        Repo.Adapter.TestRuns.Enqueue(FakeLanguageAdapter.CompileError());
        var (_, llm) = await Run(2, Respond.Tool("finish", new { reason = "x" }), Respond.Tool("finish", new { reason = "y" }));
        llm.AssertToolResultContains(1, "1 compile error");
    }

    [Fact]
    public async Task Finish_with_an_empty_test_file_is_rejected()
    {
        Repo.Adapter.TestRuns.Enqueue(FakeLanguageAdapter.Empty());
        var (_, llm) = await Run(2, Respond.Tool("finish", new { reason = "x" }), Respond.Tool("finish", new { reason = "y" }));
        llm.AssertToolResultContains(1, "hiç test çalışmadı (Passed == 0)");
    }

    [Fact]
    public async Task Finish_when_all_tests_are_skipped_is_rejected()
    {
        Repo.Adapter.TestRuns.Enqueue(FakeLanguageAdapter.AllSkipped());
        var (_, llm) = await Run(2, Respond.Tool("finish", new { reason = "x" }), Respond.Tool("finish", new { reason = "y" }));
        llm.AssertToolResultContains(1, "Passed == 0");
    }

    [Fact]
    public async Task Finish_with_one_passing_test_is_accepted()
    {
        Repo.Adapter.TestRuns.Enqueue(FakeLanguageAdapter.Green(1, 40));
        var (outcome, _) = await Run(1, Respond.Tool("finish", new { reason = "minimal" }));
        Assert.Equal(StopReason.Finish, outcome.StopReason);
    }

    [Fact]
    public async Task Three_finish_rejections_in_a_row_end_in_stagnation()
    {
        Repo.Adapter.DefaultRun = FakeLanguageAdapter.Red(1, 1, 50);
        var (outcome, _) = await Run(25,
            Respond.Tool("finish", new { reason = "1" }),
            Respond.Tool("finish", new { reason = "2" }),
            Respond.Tool("finish", new { reason = "3" }));
        Assert.Equal(StopReason.Stagnation, outcome.StopReason);
        Assert.Equal(3, outcome.Iterations.Count);
        Assert.StartsWith("stagnation:", outcome.StopDetail);
    }
}

public class AgentStagnationTests : AgentTestBase
{
    [Fact]
    public async Task Running_tests_three_times_without_edits_and_same_metrics_is_stagnation_a()
    {
        Repo.Adapter.DefaultRun = FakeLanguageAdapter.Red(2, 1, 60);
        var (outcome, _) = await Run(25, Respond.Tool("run_tests"), Respond.Tool("run_tests"), Respond.Tool("run_tests"));
        Assert.Equal(StopReason.Stagnation, outcome.StopReason);
        Assert.Contains("same failed/coverage", outcome.StopDetail);
    }

    [Fact]
    public async Task Editing_every_iteration_with_flat_coverage_is_not_stagnation()
    {
        Repo.Write(TestFile, "v0");
        Repo.Adapter.DefaultRun = FakeLanguageAdapter.Green(3, 60);
        var (outcome, _) = await Run(4,
            Respond.Tools(("edit_file", Edit("v0", "v1")), ("run_tests", null)),
            Respond.Tools(("edit_file", Edit("v1", "v2")), ("run_tests", null)),
            Respond.Tools(("edit_file", Edit("v2", "v3")), ("run_tests", null)),
            Respond.Tools(("edit_file", Edit("v3", "v4")), ("run_tests", null)));
        Assert.Equal(StopReason.MaxIterations, outcome.StopReason);
        Assert.All(outcome.Iterations, i => Assert.True(i.HasEditOrWrite && i.HasRunTests));
    }

    [Fact]
    public async Task Only_viewing_and_searching_for_three_iterations_is_stagnation_b()
    {
        var look = Respond.Tools(("view_file", new { path = "Lib/Foo.cs" }), ("search_file", new { name_pattern = "*.cs" }));
        var (outcome, _) = await Run(25, look, look, look);
        Assert.Equal(StopReason.Stagnation, outcome.StopReason);
        Assert.Contains("no run_tests and no file changes", outcome.StopDetail);
    }

    [Fact]
    public async Task Viewing_plus_running_tests_each_iteration_is_not_stagnation_b()
    {
        // Coverage changes every run so (a) cannot trigger either; only (b) is under test here.
        Repo.Adapter.TestRuns.Enqueue(FakeLanguageAdapter.Green(3, 60));
        Repo.Adapter.TestRuns.Enqueue(FakeLanguageAdapter.Green(3, 61));
        Repo.Adapter.TestRuns.Enqueue(FakeLanguageAdapter.Green(3, 62));
        var step = Respond.Tools(("view_file", new { path = "Lib/Foo.cs" }), ("run_tests", null));
        var (outcome, _) = await Run(3, step, step, step);
        Assert.Equal(StopReason.MaxIterations, outcome.StopReason);
    }

    [Fact]
    public async Task Failed_edits_do_not_count_as_progress()
    {
        Repo.Write(TestFile, "content");
        var badEdit = Respond.Tool("edit_file", Edit("not-there", "x"));
        var (outcome, _) = await Run(25, badEdit, badEdit, badEdit);
        Assert.Equal(StopReason.Stagnation, outcome.StopReason);
    }

    [Fact]
    public async Task Hitting_the_iteration_budget_stops_with_max_iterations()
    {
        Repo.Adapter.TestRuns.Enqueue(FakeLanguageAdapter.Green(1, 10));
        Repo.Adapter.TestRuns.Enqueue(FakeLanguageAdapter.Green(2, 20));
        var (outcome, _) = await Run(2, Respond.Tool("run_tests"), Respond.Tool("run_tests"));
        Assert.Equal(StopReason.MaxIterations, outcome.StopReason);
        Assert.StartsWith("max_iterations", outcome.StopDetail);
    }
}

public class ScriptedLlmClientTests
{
    [Fact]
    public async Task Throws_when_called_more_often_than_scripted()
    {
        var llm = new ScriptedLlmClient(Respond.Text("one"));
        await llm.GetCompletionAsync([LlmMessage.User("a")], [], default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => llm.GetCompletionAsync([LlmMessage.User("b")], [], default));
    }

    [Fact]
    public async Task Records_deep_copies_of_the_messages()
    {
        var llm = new ScriptedLlmClient(Respond.Text("one"));
        var messages = new List<LlmMessage> { LlmMessage.User("a") };
        await llm.GetCompletionAsync(messages, [], default);
        messages.Add(LlmMessage.User("b"));
        Assert.Single(llm.Calls[0]);
    }
}
