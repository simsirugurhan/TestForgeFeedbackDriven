using System.Text.RegularExpressions;
using TestForge.Core.Adapters;
using TestForge.Core.Feedback;
using TestForge.Core.Workspace;

namespace TestForge.Adapters.CSharp;

/// <summary>C# / .NET adapter: xUnit tests, coverlet ("XPlat Code Coverage") line coverage.</summary>
public sealed partial class CSharpAdapter : ILanguageAdapter
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    public string Name => "csharp";

    public string FrameworkHint => """
        Language: C# (.NET). Test framework: xUnit ([Fact], [Theory] + [InlineData], Assert.Equal/True/Throws...).
        The test project already references the project under test and the xUnit packages. Do not add NuGet
        packages (no FluentAssertions, Moq, ...) unless the test project file already references them.
        Use `decimal` literals with the m suffix where the code uses decimal. Put tests in a public class
        whose name ends with "Tests"; use a namespace matching the test project.
        """;

    public bool CanHandle(string repoRoot, string sourceFile) =>
        sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public string GetTestFilePath(string repoRoot, string sourceFile, string testProject) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(testProject))!,
            Path.GetFileNameWithoutExtension(sourceFile) + "Tests.cs");

    public async Task<BuildResult> BuildAsync(string repoRoot, string testProject, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("dotnet",
            ["build", testProject, "--nologo", "-v", "q", "-clp:NoSummary"], repoRoot, BuildTimeout, ct);
        if (result.TimedOut) return BuildResult.Failure(["dotnet build zaman aşımına uğradı."], result.Combined);
        return result.ExitCode == 0
            ? BuildResult.Success(result.Combined)
            : BuildResult.Failure(ExtractErrors(result.Combined, repoRoot), OutputTrimmer.Trim(result.Combined));
    }

    public async Task<TestRunResult> RunTestsAsync(string repoRoot, string testProject, string sourceFile, CancellationToken ct)
    {
        var resultsDir = Path.Combine(Path.GetTempPath(), "testforge", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(resultsDir);
        try
        {
            var result = await ProcessRunner.RunAsync("dotnet",
            [
                "test", testProject, "--nologo",
                "--collect:XPlat Code Coverage",
                "--results-directory", resultsDir,
                "--logger", "trx;LogFileName=testforge.trx",
            ], repoRoot, TestTimeout, ct);

            if (result.TimedOut)
                return TestRunResult.BuildFailure(["dotnet test zaman aşımına uğradı (sonsuz döngü ya da bekleyen test olabilir)."], result.Combined);

            var trx = Directory.EnumerateFiles(resultsDir, "*.trx", SearchOption.AllDirectories).FirstOrDefault();
            if (trx is null)
            {
                var errors = ExtractErrors(result.Combined, repoRoot);
                return errors.Count > 0 || result.ExitCode != 0
                    ? TestRunResult.BuildFailure(errors, OutputTrimmer.Trim(result.Combined))
                    : new TestRunResult(0, 0, 0, true, [], result.Combined, ReadCoverage(resultsDir, sourceFile));
            }

            var summary = TrxParser.Parse(await File.ReadAllTextAsync(trx, ct));
            var raw = summary.Failed > 0 ? summary.FailureDetails : string.Empty;
            return new TestRunResult(summary.Passed, summary.Failed, summary.Skipped, true, [], raw,
                ReadCoverage(resultsDir, sourceFile));
        }
        finally
        {
            try { Directory.Delete(resultsDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static CoverageReport? ReadCoverage(string resultsDir, string sourceFile)
    {
        var file = Directory.EnumerateFiles(resultsDir, "coverage.cobertura.xml", SearchOption.AllDirectories).FirstOrDefault();
        return file is null ? null : CoberturaParser.Parse(File.ReadAllText(file), sourceFile);
    }

    /// <summary>"path(line,col): error CS1002: ; expected [proj]" → "Foo.cs(12,5): error CS1002: ; expected" (deduplicated).</summary>
    internal static List<string> ExtractErrors(string output, string repoRoot) =>
        output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => ErrorLine().IsMatch(l))
            .Select(l => ProjectSuffix().Replace(l, string.Empty))
            .Select(l => l.Replace(repoRoot + Path.DirectorySeparatorChar, string.Empty, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

    [GeneratedRegex(@":\s*error\s+[A-Z]+\d+\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"\s*\[[^\]]+\.csproj[^\]]*\]\s*$")]
    private static partial Regex ProjectSuffix();
}
