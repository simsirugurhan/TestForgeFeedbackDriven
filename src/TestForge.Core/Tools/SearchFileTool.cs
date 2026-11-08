using System.Text;

namespace TestForge.Core.Tools;

public sealed class SearchFileTool : ITool
{
    private const int MaxResults = 100;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", ".testforge", "TestResults", "node_modules",
    };

    public string Name => "search_file";

    public string Description =>
        "Search the repository. name_pattern matches file names with wildcards (prefix 'Discount*', suffix '*Tests.cs'). " +
        "Optional contains_text additionally searches file contents (case-insensitive) and returns matching lines. " +
        "Optional directory limits the search to a sub folder.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "name_pattern": { "type": "string", "description": "File name wildcard, e.g. '*.cs', 'Discount*', '*Service.cs'. Default '*'." },
            "contains_text": { "type": "string", "description": "Optional text to search for inside files." },
            "directory": { "type": "string", "description": "Optional repo-relative directory. Default: repository root." }
          }
        }
        """;

    public async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext context, CancellationToken ct)
    {
        var pattern = args.GetString("name_pattern") is { Length: > 0 } p ? p : "*";
        var text = args.GetString("contains_text");
        var dir = context.Sandbox.ResolveRead(args.GetString("directory") is { Length: > 0 } d ? d : ".");
        if (!Directory.Exists(dir)) return ToolResult.Error($"Klasör bulunamadı: {args.GetString("directory")}");

        var sb = new StringBuilder();
        var count = 0;
        foreach (var file in EnumerateFiles(dir, pattern))
        {
            ct.ThrowIfCancellationRequested();
            var rel = context.Sandbox.ToRelative(file);
            if (string.IsNullOrEmpty(text))
            {
                sb.AppendLine(rel);
                if (++count >= MaxResults) break;
                continue;
            }

            var lineNo = 0;
            foreach (var line in await File.ReadAllLinesAsync(file, ct))
            {
                lineNo++;
                if (!line.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                sb.AppendLine($"{rel}:{lineNo}: {line.Trim()}");
                if (++count >= MaxResults) break;
            }
            if (count >= MaxResults) break;
        }

        if (count == 0) return ToolResult.Ok("[search_file] eşleşme yok.");
        if (count >= MaxResults) sb.AppendLine($"... ilk {MaxResults} sonuç gösterildi, aramayı daralt.");
        return ToolResult.Ok(sb.ToString());
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current, pattern).Order(StringComparer.OrdinalIgnoreCase))
                yield return file;
            foreach (var sub in Directory.EnumerateDirectories(current).Order(StringComparer.OrdinalIgnoreCase).Reverse())
                if (!IgnoredDirectories.Contains(Path.GetFileName(sub)))
                    pending.Push(sub);
        }
    }
}
