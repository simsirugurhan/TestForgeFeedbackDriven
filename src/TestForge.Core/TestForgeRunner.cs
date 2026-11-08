using TestForge.Core.Adapters;
using TestForge.Core.Agent;
using TestForge.Core.Feedback;
using TestForge.Core.Llm;
using TestForge.Core.Reporting;
using TestForge.Core.Tools;
using TestForge.Core.Workspace;

namespace TestForge.Core;

public sealed record RunOptions(string RepoRoot, string SourceFile, string? TestProject, int MaxIterations = 25);

public sealed record RunResult(RunReport Report, string ReportDirectory, IReadOnlyList<string> BackedUpFiles);

/// <summary>
/// Orchestrator: resolves the test project, prepares the workspace, runs the agent, then runs the
/// suite one final time and writes the report. Lives outside the agent on purpose: the agent decides
/// when it is done, the orchestrator records what is actually true at the end.
/// </summary>
public sealed class TestForgeRunner(
    ILlmClient llm,
    IReadOnlyList<ILanguageAdapter> adapters,
    ITestProjectLocator locator,
    Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });

    public async Task<RunResult> RunAsync(RunOptions options, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var repoRoot = Path.GetFullPath(options.RepoRoot);
        if (!Directory.Exists(repoRoot)) throw new ArgumentException($"Repo klasörü bulunamadı: {options.RepoRoot}");

        var sourceFile = Path.GetFullPath(Path.IsPathRooted(options.SourceFile) ? options.SourceFile : Path.Combine(repoRoot, options.SourceFile));
        if (!File.Exists(sourceFile)) throw new ArgumentException($"Kaynak dosya bulunamadı: {options.SourceFile}");

        var adapter = adapters.FirstOrDefault(a => a.CanHandle(repoRoot, sourceFile))
                      ?? throw new ArgumentException($"'{Path.GetFileName(sourceFile)}' için uygun dil adapter'ı yok. Desteklenen: {string.Join(", ", adapters.Select(a => a.Name))}");

        var testProject = locator.Locate(repoRoot, sourceFile, options.TestProject);
        var sandbox = new Sandbox(repoRoot, Path.GetDirectoryName(testProject)!, TimeSpan.FromSeconds(60));
        var context = new ToolContext(sandbox, adapter, sourceFile, testProject);
        var testFile = adapter.GetTestFilePath(repoRoot, sourceFile, testProject);

        _log($"Kaynak:       {sandbox.ToRelative(sourceFile)}");
        _log($"Test projesi: {sandbox.ToRelative(testProject)}");
        _log($"Test dosyası: {sandbox.ToRelative(testFile)}");

        var backups = BackupExistingTests(sandbox, testFile, started);
        foreach (var b in backups) _log($"Yedeklendi:   {b}");

        var prompt = Prompts.ZeroShot(
            sandbox.ToRelative(sourceFile),
            await File.ReadAllTextAsync(sourceFile, ct),
            sandbox.ToRelative(testProject),
            sandbox.ToRelative(testFile));

        var agent = new TestForgeAgent(llm, ToolRegistry.CreateDefault(), new AgentOptions { MaxIterations = options.MaxIterations });
        _log($"Ajan başladı (model: {llm.ModelName}, en fazla {options.MaxIterations} iterasyon)...");
        var outcome = await agent.RunAsync(context, prompt, ct);
        _log($"Ajan durdu: {outcome.StopDetail}");

        _log("Son test koşusu...");
        TestRunResult? finalRun = null;
        try { finalRun = await adapter.RunTestsAsync(repoRoot, testProject, sourceFile, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log($"Son koşu başarısız: {ex.Message}"); }

        var meta = new ReportMeta(started, DateTimeOffset.UtcNow, repoRoot, sandbox.ToRelative(sourceFile),
            sandbox.ToRelative(testProject), adapter.Name, llm.ModelName, options.MaxIterations);
        var report = RunReportWriter.Build(meta, outcome, finalRun);
        var reportDir = Path.Combine(repoRoot, ".testforge");
        await RunReportWriter.WriteAsync(report, reportDir, ct);
        return new RunResult(report, reportDir, backups);
    }

    /// <summary>
    /// Clears the way for the zero-shot write_file: the suggested test file and the template's UnitTest1.cs
    /// are moved (never deleted) to .testforge/backup/&lt;timestamp&gt;/.
    /// </summary>
    private static List<string> BackupExistingTests(Sandbox sandbox, string testFile, DateTimeOffset started)
    {
        var candidates = new[] { testFile, Path.Combine(sandbox.WriteScopeRoot, "UnitTest1.cs") }
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var moved = new List<string>();
        if (candidates.Count == 0) return moved;

        var backupDir = Path.Combine(sandbox.RepoRoot, ".testforge", "backup", started.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backupDir);
        foreach (var file in candidates)
        {
            var target = Path.Combine(backupDir, Path.GetFileName(file));
            File.Move(file, target, overwrite: true);
            moved.Add($"{sandbox.ToRelative(file)} → {sandbox.ToRelative(target)}");
        }
        return moved;
    }
}
