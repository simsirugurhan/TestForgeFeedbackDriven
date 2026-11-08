using System.Text.Json;
using TestForge.Adapters.CSharp;
using TestForge.Core;
using TestForge.Tests.Fakes;

namespace TestForge.Tests;

/// <summary>
/// Runs the real C# adapter (dotnet build/test + coverlet) and the full runner against a temporary
/// copy of samples/. The LLM is scripted, so no Azure key is needed. Slow (~1 min).
/// </summary>
[Trait("Category", "Integration")]
public sealed class IntegrationTests : IDisposable
{
    private readonly string _repo;

    public IntegrationTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "testforge-it", Guid.NewGuid().ToString("N"));
        var samples = Path.Combine(FindSolutionRoot(), "samples");
        CopyDirectory(Path.Combine(samples, "DiscountCalculator"), Path.Combine(_repo, "DiscountCalculator"));
        CopyDirectory(Path.Combine(samples, "DiscountCalculator.Tests"), Path.Combine(_repo, "DiscountCalculator.Tests"));
    }

    private const string FirstSuite = """
        using DiscountCalculator;

        namespace DiscountCalculator.Tests;

        public class DiscountServiceTests
        {
            private readonly DiscountService _sut = new();

            [Fact]
            public void Regular_customer_pays_full_price() =>
                Assert.Equal(100m, _sut.CalculateFinalPrice(100m, CustomerType.Regular));

            [Fact]
            public void Premium_customer_gets_discount() =>
                Assert.Equal(90m, _sut.CalculateFinalPrice(100m, CustomerType.Premium)); // wrong on purpose: 80
        }
        """;

    [Fact]
    public async Task Full_loop_with_real_dotnet_fixes_a_failing_test_and_raises_coverage()
    {
        var llm = new ScriptedLlmClient(
            Respond.Tool("write_file", new { path = "DiscountCalculator.Tests/DiscountServiceTests.cs", content = FirstSuite }),
            Respond.Tool("run_tests"),
            Respond.Tool("edit_file", new
            {
                path = "DiscountCalculator.Tests/DiscountServiceTests.cs",
                mode = "replace",
                old_text = "Assert.Equal(90m, _sut.CalculateFinalPrice(100m, CustomerType.Premium)); // wrong on purpose: 80",
                new_text = "Assert.Equal(80m, _sut.CalculateFinalPrice(100m, CustomerType.Premium));",
            }),
            Respond.Tool("edit_file", new
            {
                path = "DiscountCalculator.Tests/DiscountServiceTests.cs",
                mode = "insert_after",
                anchor = "private readonly DiscountService _sut = new();\n",
                text = "\n    [Fact]\n    public void Negative_price_throws() =>\n        Assert.Throws<ArgumentException>(() => _sut.CalculateFinalPrice(-1m, CustomerType.Regular));\n",
            }),
            Respond.Tool("run_tests"),
            Respond.Tool("finish", new { reason = "green" }));

        var runner = new TestForgeRunner(llm, [new CSharpAdapter()], new TestProjectLocator());
        var result = await runner.RunAsync(new RunOptions(_repo, "DiscountCalculator/DiscountService.cs", null), CancellationToken.None);

        // run_tests #1 reported the deliberately wrong assertion and real coverage of the source file.
        llm.AssertToolResultContains(2, "failed: 1");
        llm.AssertToolResultContains(2, "Expected: 90");
        llm.AssertToolResultContains(2, "DiscountCalculator/DiscountService.cs: %");
        llm.AssertToolResultContains(2, "missing lines:");
        llm.AssertToolResultContains(4, "[build] başarılı");

        var report = result.Report;
        Assert.Equal("finish", report.Outcome.StopReason);
        Assert.Equal(3, report.Outcome.FinalPassed);
        Assert.Equal(0, report.Outcome.FinalFailed);
        var coverages = report.Iterations.Select(i => i.CoveragePercent).Where(c => c > 0).Distinct().ToList();
        Assert.True(coverages.Count >= 2 && coverages[^1] > coverages[0], $"coverage should rise: {string.Join(", ", coverages)}");
        Assert.True(File.Exists(Path.Combine(_repo, ".testforge", "report.json")));
        Assert.True(File.Exists(Path.Combine(_repo, ".testforge", "report.md")));
        using var _ = JsonDocument.Parse(File.ReadAllText(Path.Combine(_repo, ".testforge", "report.json")));
    }

    [Fact]
    public async Task Build_reports_compile_errors_of_the_test_project()
    {
        var testProject = Path.Combine(_repo, "DiscountCalculator.Tests", "DiscountCalculator.Tests.csproj");
        File.WriteAllText(Path.Combine(_repo, "DiscountCalculator.Tests", "Broken.cs"), "public class Broken { int x = }");
        var build = await new CSharpAdapter().BuildAsync(_repo, testProject, CancellationToken.None);
        Assert.False(build.Succeeded);
        Assert.Contains(build.Errors, e => e.Contains("error CS"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string FindSolutionRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TestForge.slnx"))) return dir.FullName;
        throw new InvalidOperationException("TestForge.slnx bulunamadı.");
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj" or "TestResults") continue;
            if (dir.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || dir.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        }
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var parts = rel.Split(Path.DirectorySeparatorChar);
            if (parts.Any(p => p is "bin" or "obj" or "TestResults")) continue;
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
    }
}
