using System.Text;
using System.Xml.Linq;

namespace TestForge.Adapters.CSharp;

public sealed record TrxSummary(int Total, int Passed, int Failed, int Skipped, string FailureDetails);

/// <summary>Reads counts and failure messages from a Visual Studio TRX results file.</summary>
public static class TrxParser
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static TrxSummary Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var counters = doc.Descendants(Ns + "Counters").FirstOrDefault();
        int Read(string name) => int.TryParse((string?)counters?.Attribute(name), out var v) ? v : 0;

        var total = Read("total");
        var passed = Read("passed");
        var failed = Read("failed") + Read("error") + Read("timeout") + Read("aborted");
        var skipped = Math.Max(0, total - passed - failed);

        var details = new StringBuilder();
        foreach (var result in doc.Descendants(Ns + "UnitTestResult")
                     .Where(r => (string?)r.Attribute("outcome") is "Failed" or "Error" or "Timeout"))
        {
            details.AppendLine($"FAILED {(string?)result.Attribute("testName")}");
            var info = result.Descendants(Ns + "ErrorInfo").FirstOrDefault();
            var message = info?.Element(Ns + "Message")?.Value.Trim();
            var stack = info?.Element(Ns + "StackTrace")?.Value.Trim();
            if (!string.IsNullOrEmpty(message)) details.AppendLine("  " + message.Replace("\n", "\n  "));
            if (!string.IsNullOrEmpty(stack))
                details.AppendLine("  " + string.Join("\n  ", stack.Split('\n').Take(6).Select(l => l.Trim())));
        }
        return new TrxSummary(total, passed, failed, skipped, details.ToString());
    }
}
