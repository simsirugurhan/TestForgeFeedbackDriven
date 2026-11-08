namespace TestForge.Core.Tools;

/// <summary>Creates NEW files only. Existing files must be changed with edit_file.</summary>
public sealed class WriteFileTool : ITool
{
    public string Name => "write_file";

    public string Description =>
        "Create a NEW file inside the test project. Fails if the file already exists — use edit_file to change existing files. " +
        "Source files cannot be written.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Repo-relative path of the new file (inside the test project)." },
            "content": { "type": "string", "description": "Full content of the new file." }
          },
          "required": ["path", "content"]
        }
        """;

    public async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext context, CancellationToken ct)
    {
        var path = args.Require("path");
        var content = args.Require("content");
        var full = context.Sandbox.ResolveWrite(path);

        if (File.Exists(full))
            return ToolResult.Error(
                $"'{path}' zaten var. Mevcut dosyayı değiştirmek için edit_file kullan (tek eşleşmeli replace + insert_after). " +
                "write_file yalnızca yeni dosya oluşturur.");

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, ct);
        var rel = context.Sandbox.ToRelative(full);
        var lineCount = content.Split('\n').Length;
        return ToolResult.Ok($"[write_file OK] {rel} oluşturuldu ({lineCount} satır).") with { WrittenPath = rel };
    }
}
