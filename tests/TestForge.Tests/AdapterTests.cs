using TestForge.Adapters.CSharp;
using TestForge.Core.Adapters;
using TestForge.Tests.Fakes;

namespace TestForge.Tests;

public class CoberturaParserTests
{
    private const string Xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.5" version="1.9">
          <sources><source>C:\repo\</source></sources>
          <packages>
            <package name="Lib">
              <classes>
                <class name="Lib.Foo" filename="Lib\Foo.cs" line-rate="0.5">
                  <methods><method name="Add"><lines><line number="4" hits="1" /></lines></method></methods>
                  <lines>
                    <line number="4" hits="1" />
                    <line number="5" hits="0" />
                    <line number="6" hits="0" />
                  </lines>
                </class>
                <class name="Lib.Foo/&lt;Nested&gt;" filename="Lib\Foo.cs" line-rate="1">
                  <lines>
                    <line number="6" hits="3" />
                    <line number="9" hits="0" />
                  </lines>
                </class>
                <class name="Lib.Bar" filename="Lib\Bar.cs" line-rate="0">
                  <lines><line number="1" hits="0" /></lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;

    [Fact]
    public void Merges_classes_of_the_same_file_and_lists_missing_lines()
    {
        var report = CoberturaParser.Parse(Xml, @"C:\repo\Lib\Foo.cs");
        Assert.NotNull(report);
        Assert.Equal([4, 6], report.CoveredLines);
        Assert.Equal([5, 9], report.MissingLines);
        Assert.Equal(50.0, report.LinePercent);
    }

    [Fact]
    public void Coverage_is_measured_for_the_source_file_only()
    {
        var bar = CoberturaParser.Parse(Xml, @"C:\repo\Lib\Bar.cs");
        Assert.NotNull(bar);
        Assert.Equal(0.0, bar.LinePercent);
        Assert.Null(CoberturaParser.Parse(Xml, @"C:\repo\Lib\Missing.cs"));
    }
}

public class TrxParserTests
{
    [Fact]
    public void Reads_counts_and_failure_messages()
    {
        const string trx = """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="Lib.Tests.FooTests.Adds" outcome="Passed" />
                <UnitTestResult testName="Lib.Tests.FooTests.Fails" outcome="Failed">
                  <Output><ErrorInfo><Message>Assert.Equal() Failure: Expected 3, Actual 4</Message><StackTrace>at FooTests.Fails() in FooTests.cs:line 12</StackTrace></ErrorInfo></Output>
                </UnitTestResult>
              </Results>
              <ResultSummary outcome="Failed">
                <Counters total="4" executed="3" passed="2" failed="1" error="0" timeout="0" aborted="0" notExecuted="1" />
              </ResultSummary>
            </TestRun>
            """;
        var summary = TrxParser.Parse(trx);
        Assert.Equal((4, 2, 1, 1), (summary.Total, summary.Passed, summary.Failed, summary.Skipped));
        Assert.Contains("FAILED Lib.Tests.FooTests.Fails", summary.FailureDetails);
        Assert.Contains("Expected 3, Actual 4", summary.FailureDetails);
    }
}

public class TestProjectLocatorTests
{
    private readonly TestProjectLocator _locator = new();

    [Fact]
    public void Single_test_project_is_found_automatically()
    {
        using var repo = new TempRepo();
        Assert.Equal(repo.TestProject, _locator.Locate(repo.Root, repo.SourceFile, null));
    }

    [Fact]
    public void Explicit_test_project_is_validated_and_made_absolute()
    {
        using var repo = new TempRepo();
        Assert.Equal(repo.TestProject, _locator.Locate(repo.Root, repo.SourceFile, "Lib.Tests/Lib.Tests.csproj"));
        Assert.Equal(repo.TestProject, _locator.Locate(repo.Root, repo.SourceFile, "Lib.Tests"));
        Assert.Throws<TestProjectNotFoundException>(() => _locator.Locate(repo.Root, repo.SourceFile, "Nope/Nope.csproj"));
    }

    [Fact]
    public void No_candidate_gives_an_actionable_error()
    {
        using var repo = new TempRepo();
        File.Delete(repo.TestProject);
        var ex = Assert.Throws<TestProjectNotFoundException>(() => _locator.Locate(repo.Root, repo.SourceFile, null));
        Assert.Contains("--test-project", ex.Message);
        Assert.Contains("*.Tests.csproj", ex.Message);
    }

    [Fact]
    public void Several_candidates_are_disambiguated_by_the_source_project_name()
    {
        using var repo = new TempRepo();
        repo.Write("Other.Tests/Other.Tests.csproj", "<Project />");
        Assert.Equal(repo.TestProject, _locator.Locate(repo.Root, repo.SourceFile, null));
    }

    [Fact]
    public void Ambiguous_candidates_are_reported_instead_of_guessed()
    {
        using var repo = new TempRepo();
        repo.Write("A.Tests/A.Tests.csproj", "<Project />");
        repo.Write("Loose/Loose.cs", "class Loose {}");
        var ex = Assert.Throws<TestProjectNotFoundException>(() => _locator.Locate(repo.Root, Path.Combine(repo.Root, "Loose", "Loose.cs"), null));
        Assert.Contains("belirsiz", ex.Message);
    }
}

public class CSharpAdapterTests
{
    private readonly CSharpAdapter _adapter = new();

    [Fact]
    public void Suggested_test_file_lives_in_the_test_project()
    {
        var path = _adapter.GetTestFilePath(@"C:\repo", @"C:\repo\Lib\Foo.cs", @"C:\repo\Lib.Tests\Lib.Tests.csproj");
        Assert.Equal(Path.Combine(@"C:\repo\Lib.Tests", "FooTests.cs"), path);
    }

    [Fact]
    public void Compile_errors_are_extracted_and_deduplicated()
    {
        const string output = """
            C:\repo\Lib.Tests\FooTests.cs(3,10): error CS1002: ; expected [C:\repo\Lib.Tests\Lib.Tests.csproj]
            C:\repo\Lib.Tests\FooTests.cs(3,10): error CS1002: ; expected [C:\repo\Lib.Tests\Lib.Tests.csproj]
            C:\repo\Lib.Tests\FooTests.cs(5,1): warning CS0168: unused
            """;
        var errors = CSharpAdapter.ExtractErrors(output, @"C:\repo");
        Assert.Equal([@"Lib.Tests\FooTests.cs(3,10): error CS1002: ; expected"], errors);
    }

    [Fact]
    public async Task Mutation_is_not_supported_in_v1()
    {
        Assert.Null(await ((ILanguageAdapter)_adapter).RunMutationAsync("", "", "", default));
    }
}
