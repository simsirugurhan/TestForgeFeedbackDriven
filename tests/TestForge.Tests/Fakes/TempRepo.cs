using TestForge.Core.Adapters;
using TestForge.Core.Tools;
using TestForge.Core.Workspace;

namespace TestForge.Tests.Fakes;

/// <summary>
/// Throw-away repository on disk:
///   Lib/Lib.csproj, Lib/Foo.cs                (source, read-only for the agent)
///   Lib.Tests/Lib.Tests.csproj               (WriteScope)
/// </summary>
public sealed class TempRepo : IDisposable
{
    public TempRepo()
    {
        Root = Path.Combine(Path.GetTempPath(), "testforge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "Lib"));
        Directory.CreateDirectory(Path.Combine(Root, "Lib.Tests"));
        File.WriteAllText(Path.Combine(Root, "Lib", "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(SourceFile, "namespace Lib;\npublic class Foo\n{\n    public int Add(int a, int b) => a + b;\n}\n");
        File.WriteAllText(TestProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Adapter = new FakeLanguageAdapter();
        Sandbox = new Sandbox(Root, Path.Combine(Root, "Lib.Tests"));
        Context = new ToolContext(Sandbox, Adapter, SourceFile, TestProject);
    }

    public string Root { get; }
    public string SourceFile => Path.Combine(Root, "Lib", "Foo.cs");
    public string TestProject => Path.Combine(Root, "Lib.Tests", "Lib.Tests.csproj");
    public FakeLanguageAdapter Adapter { get; }
    public Sandbox Sandbox { get; }
    public ToolContext Context { get; }

    public string Write(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    public Task<ToolResult> Run(ITool tool, object args) =>
        tool.ExecuteAsync(ToolArgs.FromObject(args), Context, CancellationToken.None);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
