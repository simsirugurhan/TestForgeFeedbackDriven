using System.Diagnostics;
using System.Text;

namespace TestForge.Core.Workspace;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public string Combined => string.IsNullOrEmpty(StdErr) ? StdOut : $"{StdOut}\n{StdErr}".Trim();
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        // Keep dotnet CLI output stable and in English so parsers and the LLM see consistent text.
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            if (!timedOut) throw;
        }

        // Make sure async readers flushed.
        if (!timedOut) process.WaitForExit();
        return new ProcessResult(timedOut ? -1 : process.ExitCode, stdout.ToString(), stderr.ToString(), timedOut);
    }
}
