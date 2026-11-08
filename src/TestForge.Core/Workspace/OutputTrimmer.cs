namespace TestForge.Core.Workspace;

/// <summary>Keeps the head and tail of long tool output so the context window does not explode.</summary>
public static class OutputTrimmer
{
    public const int DefaultMaxChars = 12_000;

    public static string Trim(string? text, int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text ?? string.Empty;
        var head = maxChars * 2 / 3;
        var tail = maxChars - head;
        var omitted = text.Length - head - tail;
        return $"{text[..head]}\n... [{omitted} karakter kırpıldı] ...\n{text[^tail..]}";
    }
}
