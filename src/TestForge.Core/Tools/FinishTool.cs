namespace TestForge.Core.Tools;

/// <summary>
/// finish is a proposal, not a command: the suite is run once more and the request is
/// accepted only when it builds, nothing fails and at least one test passed.
/// </summary>
public sealed class FinishTool : ITool
{
    public const string RejectedPrefix = "finish reddedildi";

    public string Name => "finish";

    public string Description =>
        "Propose that the test suite is complete. The suite is run again; finish is accepted only if it builds, " +
        "0 tests fail and at least 1 test passes. Give a short reason (e.g. 'all tests pass, 100% line coverage').";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "reason": { "type": "string", "description": "Why the suite is complete." }
          },
          "required": ["reason"]
        }
        """;

    public async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext context, CancellationToken ct)
    {
        var reason = args.GetString("reason") ?? string.Empty;
        var run = await context.Adapter.RunTestsAsync(context.Sandbox.RepoRoot, context.TestProject, context.SourceFile, ct);
        var details = TestRunFormatter.Format(run, context.Sandbox, "[finish doğrulama]");

        if (run.IsGreen)
            return ToolResult.Ok($"[finish] kabul edildi. Gerekçe: {reason}\n{details}") with
            {
                TestRun = run,
                Finish = new FinishDecision(true, reason),
            };

        var why = run.BuildSucceeded && run.Failed == 0 && run.Passed == 0
            ? $"{RejectedPrefix}: hiç test çalışmadı (Passed == 0). Önce en az bir test yaz ve geçtiğinden emin ol."
            : $"{RejectedPrefix}: testler hâlâ kırmızı ({run.Failed} failed, {run.CompileErrors.Count} compile error). " +
              "Önce hataları düzelt, sonra finish çağır.";

        return ToolResult.Error($"{why}\n{details}") with
        {
            TestRun = run,
            Finish = new FinishDecision(false, reason),
        };
    }
}
