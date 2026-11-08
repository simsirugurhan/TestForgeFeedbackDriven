using TestForge.Core.Workspace;

namespace TestForge.Core.Tools;

/// <summary>
/// "execute_bash" from the paper, restricted to read-only exploration. Tests run only via
/// run_tests and builds happen automatically inside edit_file, so the agent always gets
/// structured feedback instead of raw, coverage-less output.
/// </summary>
public sealed class ExecuteShellTool : ITool
{
    public const string BuildRejected =
        "execute_bash salt keşif içindir. Derleme edit_file sonrası otomatik yapılır; ayrıca build çağırma.";
    public const string TestRejected =
        "execute_bash test çalıştıramaz. Test için run_tests kullan (coverage da run_tests ile gelir).";
    public const string ChainRejected =
        "execute_bash komut zinciri, pipe, yönlendirme veya değişken kabul etmez (; & | > < ` $). Tek bir keşif komutu çalıştır.";

    private static readonly string[] FileCommands = ["ls", "dir", "cat", "type"];
    private static readonly string[] ExactDotnetCommands = ["dotnet --info", "dotnet --list-sdks", "dotnet sln list"];
    private static readonly char[] ForbiddenChars = [';', '&', '|', '>', '<', '`', '$', '\n', '\r'];

    public string Name => "execute_bash";

    public string Description =>
        "Run ONE read-only exploration command in the repository root. Allowed: " +
        "ls, dir, cat, type (with repo paths), dotnet --info, dotnet --list-sdks, dotnet sln list. " +
        "Cannot build or run tests: use run_tests for tests; builds happen automatically after edit_file.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "The single read-only command to run, e.g. 'ls src' or 'cat src/Foo.cs'." }
          },
          "required": ["command"]
        }
        """;

    /// <summary>Returns null when allowed, otherwise the rejection message.</summary>
    public static string? Validate(string command, Sandbox sandbox)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0) return "Komut boş olamaz.";
        if (trimmed.IndexOfAny(ForbiddenChars) >= 0) return ChainRejected;

        var tokens = Tokenize(trimmed);
        var first = tokens[0].ToLowerInvariant();
        var second = tokens.Count > 1 ? tokens[1].ToLowerInvariant() : string.Empty;

        if (first is "dotnet" or "dotnet.exe")
        {
            if (second is "build" or "msbuild" or "publish" or "pack") return BuildRejected;
            if (second is "test" or "run" or "watch" or "vstest" or "stryker") return TestRejected;
            var normalized = string.Join(' ', tokens).ToLowerInvariant();
            return ExactDotnetCommands.Contains(normalized) ? null : Unknown();
        }

        if (first is "dotnet-stryker" or "stryker" or "msbuild" or "vstest.console") return first == "msbuild" ? BuildRejected : TestRejected;

        if (!FileCommands.Contains(first)) return Unknown();

        foreach (var arg in tokens.Skip(1).Where(a => !a.StartsWith('-')))
        {
            try { sandbox.ResolveRead(arg.Trim('"', '\'')); }
            catch (SandboxViolationException ex) { return ex.Message; }
        }
        return null;
    }

    public async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext context, CancellationToken ct)
    {
        var command = args.Require("command");
        var rejection = Validate(command, context.Sandbox);
        if (rejection is not null) return ToolResult.Error($"[execute_bash] reddedildi: {rejection}");

        var tokens = Tokenize(command.Trim());
        ProcessResult result;
        if (tokens[0].StartsWith("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            result = await ProcessRunner.RunAsync("dotnet", tokens.Skip(1), context.Sandbox.RepoRoot, context.Sandbox.CommandTimeout, ct);
        }
        else if (OperatingSystem.IsWindows())
        {
            result = await ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command.Trim()],
                context.Sandbox.RepoRoot, context.Sandbox.CommandTimeout, ct);
        }
        else
        {
            var unixCommand = tokens[0].ToLowerInvariant() switch { "dir" => "ls", "type" => "cat", var t => t };
            result = await ProcessRunner.RunAsync(unixCommand, tokens.Skip(1), context.Sandbox.RepoRoot, context.Sandbox.CommandTimeout, ct);
        }

        var header = result.TimedOut ? "[execute_bash] zaman aşımı" : $"[execute_bash] exit code: {result.ExitCode}";
        var body = OutputTrimmer.Trim(result.Combined);
        var output = $"{header}\n{body}";
        return result.ExitCode == 0 ? ToolResult.Ok(output) : ToolResult.Error(output);
    }

    private static string Unknown() =>
        "execute_bash salt keşif içindir. İzin verilen komutlar: ls, dir, cat, type, dotnet --info, dotnet --list-sdks, dotnet sln list. " +
        "Derleme edit_file içinde otomatik yapılır; test için run_tests kullan.";

    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        foreach (var c in command)
        {
            if (quote is not null)
            {
                if (c == quote) quote = null; else current.Append(c);
            }
            else if (c is '"' or '\'') quote = c;
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
