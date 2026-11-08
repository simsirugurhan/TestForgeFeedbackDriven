using TestForge.Core.Adapters;

namespace TestForge.Adapters.CSharp;

/// <summary>
/// Finds the *.Tests.csproj for a source file. With --test-project the path is validated; without it the
/// repository is searched: one candidate wins, several candidates are disambiguated by the source file's
/// own project name (X.csproj → X.Tests.csproj).
/// </summary>
public sealed class TestProjectLocator : ITestProjectLocator
{
    public const string NotFoundMessage =
        "Test projesi bulunamadı. --test-project <yol> verin veya repo altına bir *.Tests.csproj ekleyin.";

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".testforge", "node_modules",
    };

    public string Locate(string repoRoot, string sourceFile, string? testProject)
    {
        repoRoot = Path.GetFullPath(repoRoot);
        return string.IsNullOrWhiteSpace(testProject)
            ? Discover(repoRoot, Path.GetFullPath(sourceFile))
            : Validate(repoRoot, testProject);
    }

    private static string Validate(string repoRoot, string testProject)
    {
        var candidates = Path.IsPathRooted(testProject)
            ? [testProject]
            : new[] { Path.Combine(repoRoot, testProject), Path.GetFullPath(testProject) };

        foreach (var candidate in candidates.Select(Path.GetFullPath))
        {
            if (File.Exists(candidate) && candidate.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                return candidate;
            if (Directory.Exists(candidate))
            {
                var projects = Directory.GetFiles(candidate, "*.csproj");
                if (projects.Length == 1) return Path.GetFullPath(projects[0]);
            }
        }
        throw new TestProjectNotFoundException($"--test-project '{testProject}' bulunamadı (bir .csproj dosyası ya da tek .csproj içeren klasör olmalı).");
    }

    private static string Discover(string repoRoot, string sourceFile)
    {
        var candidates = FindTestProjects(repoRoot).ToList();
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count == 0) throw new TestProjectNotFoundException(NotFoundMessage);

        var owner = FindOwningProject(repoRoot, sourceFile);
        if (owner is not null)
        {
            var expected = Path.GetFileNameWithoutExtension(owner) + ".Tests.csproj";
            var matches = candidates.Where(c => Path.GetFileName(c).Equals(expected, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1) return matches[0];
        }

        var list = string.Join(", ", candidates.Select(c => Path.GetRelativePath(repoRoot, c)));
        throw new TestProjectNotFoundException($"{NotFoundMessage} Birden fazla aday var ve eşleşme belirsiz: {list}");
    }

    private static IEnumerable<string> FindTestProjects(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(dir, "*.Tests.csproj")) yield return Path.GetFullPath(file);
            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (!IgnoredDirectories.Contains(Path.GetFileName(sub))) pending.Push(sub);
        }
    }

    /// <summary>Walks up from the source file to the nearest folder containing a .csproj.</summary>
    private static string? FindOwningProject(string repoRoot, string sourceFile)
    {
        for (var dir = Path.GetDirectoryName(sourceFile); dir is not null && dir.Length >= repoRoot.Length; dir = Path.GetDirectoryName(dir))
        {
            var projects = Directory.GetFiles(dir, "*.csproj");
            if (projects.Length == 1) return projects[0];
        }
        return null;
    }
}
