using System.Text;

namespace TestForge.Core.Tools;

public sealed class ViewFileTool : ITool
{
    public const int WindowSize = 400;

    public string Name => "view_file";

    public string Description =>
        $"Read a file in the repository with line numbers. Shows at most {WindowSize} lines starting at start_line. " +
        "Use it to read the source under test, uncovered line ranges, or existing tests.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Repo-relative path of the file." },
            "start_line": { "type": "integer", "description": "1-based first line to show. Default 1." }
          },
          "required": ["path"]
        }
        """;

    public async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext context, CancellationToken ct)
    {
        var path = args.Require("path");
        var full = context.Sandbox.ResolveRead(path);
        if (!File.Exists(full)) return ToolResult.Error($"'{path}' bulunamadı.");

        var lines = await File.ReadAllLinesAsync(full, ct);
        var start = Math.Max(1, args.GetInt("start_line") ?? 1);
        if (lines.Length == 0) return ToolResult.Ok($"[{context.Sandbox.ToRelative(full)}] (boş dosya)");
        if (start > lines.Length) return ToolResult.Error($"start_line {start} dosya uzunluğunu ({lines.Length}) aşıyor.");

        var end = Math.Min(lines.Length, start + WindowSize - 1);
        var sb = new StringBuilder();
        sb.AppendLine($"[{context.Sandbox.ToRelative(full)}] satır {start}-{end} / {lines.Length}");
        for (var i = start; i <= end; i++) sb.Append(i.ToString().PadLeft(5)).Append(": ").AppendLine(lines[i - 1]);
        if (end < lines.Length) sb.AppendLine($"... devamı için start_line={end + 1}");
        return ToolResult.Ok(sb.ToString());
    }
}
