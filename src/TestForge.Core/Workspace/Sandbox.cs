namespace TestForge.Core.Workspace;

public sealed class SandboxViolationException(string message) : UnauthorizedAccessException(message);

/// <summary>
/// Path guard for every tool. Reads are limited to the repository root; writes are limited
/// to the test project folder (WriteScope) so the agent can never modify source code.
/// </summary>
public sealed class Sandbox
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public Sandbox(string repoRoot, string writeScopeRoot, TimeSpan? commandTimeout = null)
    {
        RepoRoot = Normalize(repoRoot);
        WriteScopeRoot = Normalize(writeScopeRoot);
        CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(60);

        if (!IsUnder(WriteScopeRoot, RepoRoot))
            throw new ArgumentException($"WriteScope ({WriteScopeRoot}) must be inside the repository ({RepoRoot}).");
    }

    public string RepoRoot { get; }
    public string WriteScopeRoot { get; }
    public TimeSpan CommandTimeout { get; }

    /// <summary>Resolves a (repo-relative or absolute) path that may only be read.</summary>
    public string ResolveRead(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new SandboxViolationException("Yol boş olamaz.");

        var full = Normalize(Path.IsPathRooted(path) ? path : Path.Combine(RepoRoot, path));
        if (!IsUnder(full, RepoRoot))
            throw new SandboxViolationException($"'{path}' repository dışında. Yalnızca {RepoRoot} altı okunabilir.");
        return full;
    }

    /// <summary>Resolves a path that will be written. Must be inside the test project folder.</summary>
    public string ResolveWrite(string path)
    {
        var full = ResolveRead(path);
        if (!IsUnder(full, WriteScopeRoot))
            throw new SandboxViolationException(
                $"Yazma yalnızca test projesi altında ({ToRelative(WriteScopeRoot)}) yapılabilir. Kaynak dosyalar düzenlenemez.");
        return full;
    }

    public string ToRelative(string fullPath)
    {
        var rel = Path.GetRelativePath(RepoRoot, fullPath);
        return rel.Replace('\\', '/');
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsUnder(string path, string root) =>
        path.Equals(root, PathComparison) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
}
