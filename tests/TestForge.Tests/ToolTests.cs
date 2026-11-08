using System.Text.Json;
using TestForge.Core.Feedback;
using TestForge.Core.Llm;
using TestForge.Core.Tools;
using TestForge.Core.Workspace;
using TestForge.Tests.Fakes;

namespace TestForge.Tests;

public class SandboxTests
{
    [Fact]
    public void ResolveRead_rejects_paths_outside_the_repository()
    {
        using var repo = new TempRepo();
        Assert.Throws<SandboxViolationException>(() => repo.Sandbox.ResolveRead("../outside.txt"));
        Assert.Throws<SandboxViolationException>(() => repo.Sandbox.ResolveRead(Path.GetTempPath()));
    }

    [Fact]
    public void ResolveWrite_only_allows_the_test_project()
    {
        using var repo = new TempRepo();
        Assert.EndsWith("FooTests.cs", repo.Sandbox.ResolveWrite("Lib.Tests/FooTests.cs"));
        var ex = Assert.Throws<SandboxViolationException>(() => repo.Sandbox.ResolveWrite("Lib/Foo.cs"));
        Assert.Contains("Kaynak dosyalar düzenlenemez", ex.Message);
    }

    [Fact]
    public void Sibling_folder_with_same_prefix_is_not_inside_write_scope()
    {
        using var repo = new TempRepo();
        Assert.Throws<SandboxViolationException>(() => repo.Sandbox.ResolveWrite("Lib.TestsEvil/x.cs"));
    }
}

public class ToolRegistryTests
{
    [Fact]
    public void Default_registry_exposes_exactly_the_v1_tools_and_no_build_coverage_or_mutation_tool()
    {
        var names = ToolRegistry.CreateDefault().Names.Order().ToList();
        Assert.Equal(["edit_file", "execute_bash", "finish", "run_tests", "search_file", "view_file", "write_file"], names);
        Assert.DoesNotContain(names, n => n.Contains("build") || n.Contains("coverage") || n.Contains("mutation"));
    }

    [Fact]
    public void Every_tool_schema_is_valid_json_object()
    {
        foreach (var def in ToolRegistry.CreateDefault().Definitions)
        {
            using var doc = JsonDocument.Parse(def.ParametersJsonSchema);
            Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Unknown_tool_and_malformed_arguments_become_error_results()
    {
        using var repo = new TempRepo();
        var registry = ToolRegistry.CreateDefault();
        var unknown = await registry.ExecuteAsync(new LlmToolCall("1", "build", "{}"), repo.Context, default);
        Assert.False(unknown.Success);
        var malformed = await registry.ExecuteAsync(new LlmToolCall("2", "view_file", "{not json"), repo.Context, default);
        Assert.False(malformed.Success);
        Assert.Contains("JSON", malformed.Output);
    }
}

public class ExecuteShellToolTests
{
    private readonly ExecuteShellTool _tool = new();

    [Theory]
    [InlineData("dotnet build")]
    [InlineData("dotnet build Lib.Tests/Lib.Tests.csproj")]
    public async Task Dotnet_build_is_rejected_and_points_to_edit_file(string command)
    {
        using var repo = new TempRepo();
        var result = await repo.Run(_tool, new { command });
        Assert.False(result.Success);
        Assert.Contains("edit_file", result.Output);
    }

    [Theory]
    [InlineData("dotnet test")]
    [InlineData("dotnet run")]
    [InlineData("dotnet-stryker")]
    public async Task Test_commands_are_rejected_and_point_to_run_tests(string command)
    {
        using var repo = new TempRepo();
        var result = await repo.Run(_tool, new { command });
        Assert.False(result.Success);
        Assert.Contains("run_tests", result.Output);
    }

    [Theory]
    [InlineData("ls && dotnet build")]
    [InlineData("ls; rm -rf /")]
    [InlineData("cat Lib/Foo.cs | more")]
    [InlineData("ls > out.txt")]
    [InlineData("cat $HOME")]
    public async Task Chains_pipes_redirects_and_variables_are_rejected(string command)
    {
        using var repo = new TempRepo();
        var result = await repo.Run(_tool, new { command });
        Assert.False(result.Success);
        Assert.Contains(ExecuteShellTool.ChainRejected, result.Output);
    }

    [Theory]
    [InlineData("rm Lib/Foo.cs")]
    [InlineData("curl http://example.com")]
    [InlineData("git status")]
    public void Commands_outside_the_allowlist_are_rejected(string command)
    {
        using var repo = new TempRepo();
        Assert.NotNull(ExecuteShellTool.Validate(command, repo.Sandbox));
    }

    [Fact]
    public void File_commands_cannot_read_outside_the_repository()
    {
        using var repo = new TempRepo();
        Assert.NotNull(ExecuteShellTool.Validate("cat ../../secret.txt", repo.Sandbox));
    }

    [Fact]
    public async Task Ls_runs_in_the_repository_root()
    {
        using var repo = new TempRepo();
        var result = await repo.Run(_tool, new { command = "ls Lib" });
        Assert.True(result.Success, result.Output);
        Assert.Contains("Foo.cs", result.Output);
    }
}

public class WriteFileToolTests
{
    private readonly WriteFileTool _tool = new();

    [Fact]
    public async Task Creates_a_new_file_in_the_test_project()
    {
        using var repo = new TempRepo();
        var result = await repo.Run(_tool, new { path = "Lib.Tests/FooTests.cs", content = "class FooTests {}" });
        Assert.True(result.Success, result.Output);
        Assert.Equal("Lib.Tests/FooTests.cs", result.WrittenPath);
        Assert.Equal("class FooTests {}", repo.Read("Lib.Tests/FooTests.cs"));
    }

    [Fact]
    public async Task Refuses_to_overwrite_an_existing_file()
    {
        using var repo = new TempRepo();
        repo.Write("Lib.Tests/FooTests.cs", "original");
        var result = await repo.Run(_tool, new { path = "Lib.Tests/FooTests.cs", content = "overwritten" });
        Assert.False(result.Success);
        Assert.Contains("zaten var", result.Output);
        Assert.Contains("edit_file", result.Output);
        Assert.Equal("original", repo.Read("Lib.Tests/FooTests.cs"));
    }

    [Fact]
    public async Task Refuses_to_write_source_files()
    {
        using var repo = new TempRepo();
        var result = await ToolRegistry.CreateDefault().ExecuteAsync(
            new LlmToolCall("1", "write_file", """{"path":"Lib/New.cs","content":"x"}"""), repo.Context, default);
        Assert.False(result.Success);
        Assert.Contains("test projesi altında", result.Output);
        Assert.False(File.Exists(Path.Combine(repo.Root, "Lib", "New.cs")));
    }
}

public class EditFileToolTests
{
    private readonly EditFileTool _tool = new();
    private const string TestFile = "Lib.Tests/FooTests.cs";

    [Fact]
    public async Task Replace_requires_exactly_one_match()
    {
        using var repo = new TempRepo();
        repo.Write(TestFile, "Assert.True(x);\nAssert.True(x);\n");

        var none = await repo.Run(_tool, new { path = TestFile, mode = "replace", old_text = "missing", new_text = "y" });
        Assert.False(none.Success);
        Assert.Contains("bulunamadı", none.Output);

        var many = await repo.Run(_tool, new { path = TestFile, mode = "replace", old_text = "Assert.True(x);", new_text = "y" });
        Assert.False(many.Success);
        Assert.Contains("2 kez", many.Output);
        Assert.Equal(0, repo.Adapter.BuildCalls);
    }

    [Fact]
    public async Task Replace_applies_the_change_and_builds_the_test_project()
    {
        using var repo = new TempRepo();
        repo.Write(TestFile, "line1\nAssert.Equal(1, x);\nline3\n");

        var result = await repo.Run(_tool, new { path = TestFile, mode = "replace", old_text = "Assert.Equal(1, x);", new_text = "Assert.Equal(2, x);" });

        Assert.True(result.Success, result.Output);
        Assert.Contains("[edit_file OK]", result.Output);
        Assert.Contains("[build] başarılı", result.Output);
        Assert.False(result.HasBuildError);
        Assert.Equal(1, repo.Adapter.BuildCalls);
        Assert.Equal("line1\nAssert.Equal(2, x);\nline3\n", repo.Read(TestFile));
    }

    [Fact]
    public async Task Insert_after_inserts_behind_a_unique_anchor()
    {
        using var repo = new TempRepo();
        repo.Write(TestFile, "class FooTests\n{\n}\n");
        var result = await repo.Run(_tool, new { path = TestFile, mode = "insert_after", anchor = "{\n", text = "    [Fact] public void T() {}\n" });
        Assert.True(result.Success, result.Output);
        Assert.Equal("class FooTests\n{\n    [Fact] public void T() {}\n}\n", repo.Read(TestFile));
    }

    [Fact]
    public async Task Build_errors_are_appended_to_the_tool_result()
    {
        using var repo = new TempRepo();
        repo.Write(TestFile, "a\n");
        repo.Adapter.Builds.Enqueue(BuildResult.Failure(["FooTests.cs(1,2): error CS1002: ; expected"]));

        var result = await repo.Run(_tool, new { path = TestFile, mode = "replace", old_text = "a", new_text = "b" });

        Assert.True(result.HasBuildError);
        Assert.Contains("[build] başarısız", result.Output);
        Assert.Contains("CS1002", result.Output);
    }

    [Fact]
    public async Task Editing_source_files_is_rejected()
    {
        using var repo = new TempRepo();
        var result = await ToolRegistry.CreateDefault().ExecuteAsync(
            new LlmToolCall("1", "edit_file", """{"path":"Lib/Foo.cs","mode":"replace","old_text":"a + b","new_text":"a - b"}"""),
            repo.Context, default);
        Assert.False(result.Success);
        Assert.Contains("Kaynak dosyalar düzenlenemez", result.Output);
        Assert.Contains("a + b", repo.Read("Lib/Foo.cs"));
    }

    [Fact]
    public async Task Edits_in_two_different_test_files_each_trigger_a_build()
    {
        using var repo = new TempRepo();
        repo.Write(TestFile, "one\n");
        repo.Write("Lib.Tests/FooEdgeCaseTests.cs", "two\n");

        await repo.Run(_tool, new { path = TestFile, mode = "replace", old_text = "one", new_text = "1" });
        await repo.Run(_tool, new { path = "Lib.Tests/FooEdgeCaseTests.cs", mode = "replace", old_text = "two", new_text = "2" });

        Assert.Equal(2, repo.Adapter.BuildCalls);
    }

    [Fact]
    public async Task Line_endings_of_the_file_are_respected()
    {
        using var repo = new TempRepo();
        repo.Write(TestFile, "a\r\nb\r\n");
        var result = await repo.Run(_tool, new { path = TestFile, mode = "replace", old_text = "a\nb", new_text = "x\ny" });
        Assert.True(result.Success, result.Output);
        Assert.Equal("x\r\ny\r\n", repo.Read(TestFile));
    }
}

public class ViewAndSearchToolTests
{
    [Fact]
    public async Task View_file_shows_numbered_lines_in_a_400_line_window()
    {
        using var repo = new TempRepo();
        repo.Write("Lib/Big.cs", string.Join('\n', Enumerable.Range(1, 900).Select(i => $"// line {i}")));
        var result = await repo.Run(new ViewFileTool(), new { path = "Lib/Big.cs", start_line = 401 });
        Assert.Contains("satır 401-800 / 900", result.Output);
        Assert.Contains("  401: // line 401", result.Output);
        Assert.DoesNotContain("// line 801", result.Output);
        Assert.Contains("start_line=801", result.Output);
    }

    [Fact]
    public async Task Search_file_finds_by_name_and_by_content()
    {
        using var repo = new TempRepo();
        var byName = await repo.Run(new SearchFileTool(), new { name_pattern = "Foo*" });
        Assert.Contains("Lib/Foo.cs", byName.Output);

        var byContent = await repo.Run(new SearchFileTool(), new { name_pattern = "*.cs", contains_text = "public int add" });
        Assert.Contains("Lib/Foo.cs:4:", byContent.Output);
    }
}

public class RunTestsToolTests
{
    [Fact]
    public async Task Reports_counts_coverage_and_every_missing_line()
    {
        using var repo = new TempRepo();
        repo.Adapter.TestRuns.Enqueue(FakeLanguageAdapter.Green(5, 72.5, [3, 4, 5, 9, 12, 13]));
        var result = await repo.Run(new RunTestsTool(), new { });
        Assert.Contains("passed: 5 | failed: 0", result.Output);
        Assert.Contains("%72.5", result.Output);
        Assert.Contains("missing lines: 3-5, 9, 12-13", result.Output);
        Assert.NotNull(result.TestRun);
    }

    [Fact]
    public void Ranges_never_drop_lines()
    {
        Assert.Equal("1-3, 5, 7-8", TestRunFormatter.FormatRanges([8, 1, 2, 3, 5, 7, 2]));
    }
}
