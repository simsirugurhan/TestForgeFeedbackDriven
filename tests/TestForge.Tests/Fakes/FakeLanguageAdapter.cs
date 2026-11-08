using TestForge.Core.Adapters;
using TestForge.Core.Feedback;

namespace TestForge.Tests.Fakes;

/// <summary>Adapter that never runs dotnet: test runs and builds come from queues.</summary>
public sealed class FakeLanguageAdapter : ILanguageAdapter
{
    public Queue<TestRunResult> TestRuns { get; } = new();
    public Queue<BuildResult> Builds { get; } = new();
    public TestRunResult DefaultRun { get; set; } = Green(3, 80);
    public int BuildCalls { get; private set; }
    public int TestRunCalls { get; private set; }

    public string Name => "fake";
    public string FrameworkHint => "fake framework";

    public bool CanHandle(string repoRoot, string sourceFile) => true;

    public string GetTestFilePath(string repoRoot, string sourceFile, string testProject) =>
        Path.Combine(Path.GetDirectoryName(testProject)!, Path.GetFileNameWithoutExtension(sourceFile) + "Tests.cs");

    public Task<BuildResult> BuildAsync(string repoRoot, string testProject, CancellationToken ct)
    {
        BuildCalls++;
        return Task.FromResult(Builds.Count > 0 ? Builds.Dequeue() : BuildResult.Success());
    }

    public Task<TestRunResult> RunTestsAsync(string repoRoot, string testProject, string sourceFile, CancellationToken ct)
    {
        TestRunCalls++;
        return Task.FromResult(TestRuns.Count > 0 ? TestRuns.Dequeue() : DefaultRun);
    }

    public static TestRunResult Green(int passed, double coverage, int[]? missing = null) =>
        new(passed, 0, 0, true, [], string.Empty, Coverage(coverage, missing));

    public static TestRunResult Red(int passed, int failed, double coverage) =>
        new(passed, failed, 0, true, [], "FAILED SomeTest\n  Assert.Equal() Failure", Coverage(coverage));

    public static TestRunResult CompileError() =>
        TestRunResult.BuildFailure(["FooTests.cs(3,1): error CS1002: ; expected"], "build failed");

    public static TestRunResult Empty() => new(0, 0, 0, true, [], string.Empty, Coverage(0));

    public static TestRunResult AllSkipped() => new(0, 0, 4, true, [], string.Empty, Coverage(0));

    private static CoverageReport Coverage(double percent, int[]? missing = null) =>
        new("Lib/Foo.cs", percent, [1, 2], missing ?? [10, 11]);
}
