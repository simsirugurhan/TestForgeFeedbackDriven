using System.Text;

namespace TestForge.Core.Tools;

/// <summary>
/// Single-match edits (replace / insert_after) on files inside the test project.
/// After every successful edit the test project is built and the result is appended,
/// so the agent sees compile errors immediately without a separate build tool.
/// </summary>
public sealed class EditFileTool : ITool
{
    private const int MaxSnippetLines = 40;

    public string Name => "edit_file";

    public string Description =>
        "Edit an existing file inside the test project. mode='replace': old_text must occur EXACTLY once and is replaced by new_text. " +
        "mode='insert_after': text is inserted right after anchor, which must occur EXACTLY once. " +
        "The test project is built automatically after the edit and compile errors are returned.";

    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Repo-relative path of the file to edit (inside the test project)." },
            "mode": { "type": "string", "enum": ["replace", "insert_after"] },
            "old_text": { "type": "string", "description": "replace: exact text to replace (must be unique in the file)." },
            "new_text": { "type": "string", "description": "replace: replacement text." },
            "anchor": { "type": "string", "description": "insert_after: exact text after which to insert (must be unique)." },
            "text": { "type": "string", "description": "insert_after: text to insert." }
          },
          "required": ["path", "mode"]
        }
        """;

    public async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext context, CancellationToken ct)
    {
        var path = args.Require("path");
        var mode = args.Require("mode");
        var full = context.Sandbox.ResolveWrite(path);
        if (!File.Exists(full)) return ToolResult.Error($"'{path}' bulunamadı. Yeni dosya için write_file kullan.");

        var original = await File.ReadAllTextAsync(full, ct);
        var useCrLf = original.Contains("\r\n");

        string search, insertion;
        bool replace;
        switch (mode)
        {
            case "replace":
                search = MatchLineEndings(args.Require("old_text"), useCrLf);
                insertion = MatchLineEndings(args.GetString("new_text") ?? string.Empty, useCrLf);
                replace = true;
                break;
            case "insert_after":
                search = MatchLineEndings(args.Require("anchor"), useCrLf);
                insertion = MatchLineEndings(args.Require("text"), useCrLf);
                replace = false;
                break;
            default:
                return ToolResult.Error($"Geçersiz mode '{mode}'. 'replace' veya 'insert_after' kullan.");
        }

        if (search.Length == 0) return ToolResult.Error("Aranan metin boş olamaz.");

        var matches = CountOccurrences(original, search);
        var label = replace ? "old_text" : "anchor";
        if (matches == 0)
            return ToolResult.Error($"{label} dosyada bulunamadı. view_file ile dosyayı oku ve metni birebir (boşluklar dahil) kopyala.");
        if (matches > 1)
            return ToolResult.Error($"{label} dosyada {matches} kez geçiyor; tam bir eşleşme gerekli. Daha fazla çevre satır ekleyerek daha spesifik bir metin ver.");

        var index = original.IndexOf(search, StringComparison.Ordinal);
        var changeStart = replace ? index : index + search.Length;
        var updated = replace
            ? string.Concat(original.AsSpan(0, index), insertion, original.AsSpan(index + search.Length))
            : original.Insert(changeStart, insertion);

        await File.WriteAllTextAsync(full, updated, ct);
        var rel = context.Sandbox.ToRelative(full);

        var sb = new StringBuilder();
        sb.AppendLine($"[edit_file OK] {rel}: {Describe(updated, changeStart, insertion)}");
        sb.Append(Snippet(updated, changeStart, insertion));

        var build = await context.Adapter.BuildAsync(context.Sandbox.RepoRoot, context.TestProject, ct);
        if (build.Succeeded)
        {
            sb.AppendLine("[build] başarılı.");
            return ToolResult.Ok(sb.ToString()) with { WrittenPath = rel };
        }

        sb.AppendLine("[build] başarısız:");
        foreach (var error in build.Errors.Take(30)) sb.AppendLine("  " + error);
        if (build.Errors.Count == 0) sb.AppendLine(Workspace.OutputTrimmer.Trim(build.RawOutput, 4000));
        sb.AppendLine("Önce bu derleme hatasını düzelt.");
        return ToolResult.Ok(sb.ToString()) with { WrittenPath = rel, HasBuildError = true };
    }

    private static string MatchLineEndings(string text, bool useCrLf) =>
        useCrLf ? text.Replace("\r\n", "\n").Replace("\n", "\r\n") : text.Replace("\r\n", "\n");

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string Describe(string text, int start, string insertion)
    {
        var first = LineOf(text, start);
        var count = Math.Max(1, insertion.Count(c => c == '\n') + (insertion.EndsWith('\n') ? 0 : 1));
        return insertion.Length == 0
            ? $"satır {first} civarında metin silindi."
            : $"satır {first}-{first + count - 1} değişti ({count} satır).";
    }

    private static string Snippet(string text, int start, string insertion)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var first = LineOf(text, start);
        var count = Math.Max(1, insertion.Count(c => c == '\n') + 1);
        var from = Math.Max(1, first - 2);
        var to = Math.Min(lines.Length, first + Math.Min(count, MaxSnippetLines) + 1);
        var sb = new StringBuilder();
        for (var i = from; i <= to; i++) sb.Append(i.ToString().PadLeft(5)).Append(": ").AppendLine(lines[i - 1]);
        return sb.ToString();
    }
}
