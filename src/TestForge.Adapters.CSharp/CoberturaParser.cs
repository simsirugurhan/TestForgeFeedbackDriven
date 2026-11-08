using System.Xml.Linq;
using TestForge.Core.Feedback;

namespace TestForge.Adapters.CSharp;

/// <summary>Extracts line coverage of one source file from a Cobertura XML report (coverlet output).</summary>
public static class CoberturaParser
{
    public static CoverageReport? Parse(string xml, string sourceFile)
    {
        var doc = XDocument.Parse(xml);
        var target = Normalize(Path.GetFullPath(sourceFile));
        var sources = doc.Descendants("source").Select(s => s.Value.Trim()).Where(s => s.Length > 0).ToList();

        // Same file can appear in several <class> elements (nested types, async state machines): merge them.
        var hits = new Dictionary<int, int>();
        var found = false;
        foreach (var cls in doc.Descendants("class"))
        {
            var filename = (string?)cls.Attribute("filename");
            if (filename is null || !Matches(filename, sources, target)) continue;
            found = true;

            var lines = cls.Element("lines")?.Elements("line") ?? [];
            foreach (var line in lines)
            {
                if (!int.TryParse((string?)line.Attribute("number"), out var number)) continue;
                _ = int.TryParse((string?)line.Attribute("hits"), out var h);
                hits[number] = hits.TryGetValue(number, out var existing) ? Math.Max(existing, h) : h;
            }
        }

        if (!found) return null;
        var covered = hits.Where(kv => kv.Value > 0).Select(kv => kv.Key).Order().ToList();
        var missing = hits.Where(kv => kv.Value == 0).Select(kv => kv.Key).Order().ToList();
        var total = covered.Count + missing.Count;
        var percent = total == 0 ? 100.0 : Math.Round(100.0 * covered.Count / total, 2);
        return new CoverageReport(Path.GetFullPath(sourceFile), percent, covered, missing);
    }

    private static bool Matches(string filename, List<string> sources, string target)
    {
        if (Path.IsPathRooted(filename)) return Normalize(Path.GetFullPath(filename)) == target;
        foreach (var source in sources)
        {
            try
            {
                if (Normalize(Path.GetFullPath(Path.Combine(source, filename))) == target) return true;
            }
            catch (ArgumentException) { /* malformed source entry */ }
        }
        // Fallback: relative suffix match (deterministic builds, path maps).
        return target.EndsWith("/" + Normalize(filename).TrimStart('/'), StringComparison.Ordinal);
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').ToLowerInvariant();
}
