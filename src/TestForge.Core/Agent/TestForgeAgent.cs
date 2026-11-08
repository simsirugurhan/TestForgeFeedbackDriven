using TestForge.Core.Llm;
using TestForge.Core.Tools;

namespace TestForge.Core.Agent;

public sealed record AgentOptions
{
    public int MaxIterations { get; init; } = 25;

    /// <summary>Window (in iterations) used for stagnation detection.</summary>
    public int StagnationWindow { get; init; } = 3;
}

/// <summary>
/// The TestForge loop: zero-shot test suite, then Generate → Execute → Observe → Improve → Repeat
/// driven by execution and coverage feedback, until finish is accepted, progress stalls or the
/// iteration budget is spent.
/// </summary>
public sealed class TestForgeAgent(ILlmClient llm, ToolRegistry tools, AgentOptions? options = null)
{
    private readonly AgentOptions _options = options ?? new AgentOptions();

    public async Task<AgentOutcome> RunAsync(ToolContext context, string zeroShotPrompt, CancellationToken ct)
    {
        var messages = new List<LlmMessage>
        {
            LlmMessage.System(Prompts.System(context.Adapter.FrameworkHint)),
            LlmMessage.User(zeroShotPrompt),
        };
        var iterations = new List<IterationRecord>();
        var usage = LlmUsage.Zero;

        for (var index = 1; index <= _options.MaxIterations; index++)
        {
            var record = new IterationRecord { Index = index };
            iterations.Add(record);

            LlmResponse response;
            try
            {
                response = await llm.GetCompletionAsync(messages, tools.Definitions, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new AgentOutcome(StopReason.Error, $"error: LLM çağrısı başarısız: {ex.Message}", null, iterations, usage);
            }

            record.Usage = response.Usage;
            usage = usage.Add(response.Usage);
            messages.Add(LlmMessage.Assistant(response.Content, response.ToolCalls));

            if (response.ToolCalls.Count == 0)
            {
                messages.Add(LlmMessage.User(Prompts.NoToolCallNudge));
            }
            else
            {
                var (accepted, finishReason) = await ExecuteToolCallsAsync(response.ToolCalls, record, messages, context, ct);
                if (accepted)
                    return new AgentOutcome(StopReason.Finish, "finish", finishReason, iterations, usage);
            }

            if (DetectStagnation(iterations) is { } stagnation)
                return new AgentOutcome(StopReason.Stagnation, stagnation, null, iterations, usage);
        }

        return new AgentOutcome(StopReason.MaxIterations,
            $"max_iterations: {_options.MaxIterations} iterasyon doldu", null, iterations, usage);
    }

    private async Task<(bool Accepted, string? Reason)> ExecuteToolCallsAsync(
        IReadOnlyList<LlmToolCall> calls, IterationRecord record, List<LlmMessage> messages, ToolContext context, CancellationToken ct)
    {
        var reflect = false;
        string? finishRejection = null;

        foreach (var call in calls)
        {
            var result = await tools.ExecuteAsync(call, context, ct);
            messages.Add(LlmMessage.ToolResult(call.Id, result.Output));

            var path = call.Name is "edit_file" or "write_file"
                ? result.WrittenPath ?? ToolArgsPath(call)
                : null;
            record.ToolCalls.Add(new ToolCallRecord(call.Name, path));
            if (result.TestRun is { } run) record.TestRuns.Add(TestSnapshot.From(run));

            // Reflection switch: only after calls that produce meaningful feedback.
            switch (call.Name)
            {
                case "run_tests":
                    record.HasRunTests = true;
                    reflect = true;
                    break;
                case "edit_file" or "write_file" when result.WrittenPath is not null:
                    record.HasEditOrWrite = true;
                    if (result.HasBuildError) reflect = true;
                    break;
                case "finish" when result.Finish is { Accepted: true } decision:
                    return (true, decision.Reason);
                case "finish":
                    record.FinishRejected = true;
                    finishRejection = result.Output.Split('\n')[0];
                    break;
            }
        }

        if (finishRejection is not null)
        {
            messages.Add(LlmMessage.User(Prompts.FinishRejectedReflection(finishRejection)));
            record.ReflectionAdded = true;
        }
        else if (reflect)
        {
            messages.Add(LlmMessage.User(Prompts.ReflectionTemplate));
            record.ReflectionAdded = true;
        }
        return (false, null);
    }

    /// <summary>
    /// (a) no file changed in the last N iterations and every one of them ran tests with identical (failed, coverage%);
    /// (b) the last N iterations neither ran tests nor changed a file (e.g. only view/search).
    /// A finish rejection consumes an iteration and does not reset either condition.
    /// </summary>
    internal string? DetectStagnation(IReadOnlyList<IterationRecord> iterations)
    {
        var n = _options.StagnationWindow;
        if (iterations.Count < n) return null;
        var window = iterations.Skip(iterations.Count - n).ToList();

        if (window.All(i => !i.HasRunTests && !i.HasEditOrWrite))
            return $"stagnation: no progress in {n} iterations (no run_tests and no file changes)";

        if (window.All(i => !i.HasEditOrWrite && i.TestRuns.Count > 0))
        {
            var keys = window.SelectMany(i => i.TestRuns).Select(r => r.ProgressKey).Distinct().Count();
            if (keys == 1)
                return $"stagnation: no progress in {n} iterations (same failed/coverage, no file changes)";
        }
        return null;
    }

    private static string? ToolArgsPath(LlmToolCall call)
    {
        try { return ToolArgs.Parse(call.ArgumentsJson).GetString("path")?.Replace('\\', '/'); }
        catch (ToolArgumentException) { return null; }
    }
}
