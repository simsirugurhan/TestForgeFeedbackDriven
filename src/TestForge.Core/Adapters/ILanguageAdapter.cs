using TestForge.Core.Feedback;

namespace TestForge.Core.Adapters;

/// <summary>
/// Language/test-framework specific operations. The agent core is language-agnostic;
/// a new language (Python, JS, ...) is supported by adding an adapter.
/// </summary>
public interface ILanguageAdapter
{
    string Name { get; }

    /// <summary>Short description of the test framework and conventions, injected into prompts.</summary>
    string FrameworkHint { get; }

    bool CanHandle(string repoRoot, string sourceFile);

    /// <summary>Suggested path for the first (zero-shot) test file. The agent may add more files.</summary>
    string GetTestFilePath(string repoRoot, string sourceFile, string testProject);

    /// <summary>Compile only. Not exposed to the agent as a tool; edit_file calls it after each edit.</summary>
    Task<BuildResult> BuildAsync(string repoRoot, string testProject, CancellationToken ct);

    /// <summary>Build + run tests + collect line coverage of <paramref name="sourceFile"/> in one go.</summary>
    Task<TestRunResult> RunTestsAsync(string repoRoot, string testProject, string sourceFile, CancellationToken ct);

    /// <summary>v2. v1 adapters return null and no mutation tool is registered.</summary>
    Task<MutationReport?> RunMutationAsync(string repoRoot, string testProject, string sourceFile, CancellationToken ct)
        => Task.FromResult<MutationReport?>(null);
}

public interface ITestProjectLocator
{
    /// <summary>
    /// Returns the absolute path of the test project file. Throws <see cref="TestProjectNotFoundException"/>
    /// with a user-facing message when nothing (or nothing unambiguous) is found.
    /// </summary>
    string Locate(string repoRoot, string sourceFile, string? testProject);
}

public sealed class TestProjectNotFoundException(string message) : InvalidOperationException(message);
