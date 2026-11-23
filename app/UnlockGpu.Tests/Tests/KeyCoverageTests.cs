using System.Text.Json;
using System.Text.RegularExpressions;
using UnlockGpu.Core.Infrastructure;

namespace UnlockGpu.Tests;

/// <summary>
/// Checks the merged translation tables against the source tree: every key referenced from C# or XAML
/// must exist in i18n/en.json, and en.json / pt.json must have the same keys and the same {placeholders}.
/// Reads the source files next to the repository root (found by walking up to UnlockGPU.slnx).
/// </summary>
public sealed partial class KeyCoverageTests
{
    /// <summary>Prefixes of every key family in the translation tables.</summary>
    const string KeyPrefixes = "app|ui|svc|tune|bench|vram|oem|reason";

    // T("key"), Tr("key"), loc["key"], Instance["key"] in C#.
    [GeneratedRegex("""(?:\bT|\bTr)\(\s*"([^"\\]+)"|\[\s*"([^"\\]+)"\s*\]""")]
    private static partial Regex CallPattern();

    // Any C# string literal that looks like a full key of a known family (constants such as ServiceTextKeys.*).
    [GeneratedRegex("\"((?:" + KeyPrefixes + """)\.[A-Za-z0-9_.]*[A-Za-z0-9_])"(?!\s*\+)""")]
    private static partial Regex KeyLiteralPattern();

    // {l:Tr key} or {l:Tr Key=key} in XAML.
    [GeneratedRegex("""\{l:Tr\s+(?:Key=)?([^}\s,]+)\s*\}""")]
    private static partial Regex XamlPattern();

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")]
    private static partial Regex PlaceholderPattern();

    static readonly string Root = FindRoot();

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "UnlockGPU.slnx"))) return dir.FullName;
        throw new InvalidOperationException("UnlockGPU.slnx not found above " + AppContext.BaseDirectory);
    }

    static Dictionary<string, string> Table(string code)
    {
        string path = Path.Combine(Root, "app", "UnlockGpu.Core", "i18n", code + ".json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
    }

    static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(Path.Combine(Root, "app"), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                     && !f.Contains(Path.DirectorySeparatorChar + "UnlockGpu.Tests" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    /// <summary>Collects every referenced key with the file it came from.</summary>
    static List<(string Key, string File)> ReferencedKeys()
    {
        var found = new List<(string, string)>();
        foreach (string file in SourceFiles("*.cs"))
        {
            // Comment lines hold examples such as T("x", ...); they are not references.
            string text = string.Join('\n', File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            foreach (Match m in CallPattern().Matches(text))
            {
                string key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                // Indexers are also used for resource dictionaries ("AccentBrush"); only dotted keys are translation keys.
                if (m.Groups[2].Success && !Regex.IsMatch(key, "^(?:" + KeyPrefixes + @")\.")) continue;
                found.Add((key, file));
            }
            foreach (Match m in KeyLiteralPattern().Matches(text))
                found.Add((m.Groups[1].Value, file));
        }
        foreach (string file in SourceFiles("*.xaml"))
            foreach (Match m in XamlPattern().Matches(File.ReadAllText(file)))
                found.Add((m.Groups[1].Value, file));
        return found;
    }

    [Test]
    public void SourceTree_ReferencesKeys()
    {
        var refs = ReferencedKeys();
        Check.True(refs.Count > 200, $"only {refs.Count} key references found; the scanner is probably broken");
    }

    [Test]
    public void EveryReferencedKey_ExistsInEnglish()
    {
        var en = Table("en");
        var missing = ReferencedKeys()
            .Where(r => !en.ContainsKey(r.Key))
            .Select(r => $"{r.Key} ({Path.GetRelativePath(Root, r.File)})")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (missing.Count > 0)
            throw new AssertionException($"{missing.Count} key(s) missing from en.json:\n    " + string.Join("\n    ", missing));
    }

    [Test]
    public void AllLanguages_HaveSameKeysAndPlaceholders()
    {
        var en = Table("en");
        var problems = new List<string>();
        foreach (var (code, _) in Localizer.Languages)
            if (code != "en") Compare(en, code, problems);
        if (problems.Count > 0)
            throw new AssertionException(problems.Count + " problem(s):\n    " + string.Join("\n    ", problems.Order(StringComparer.Ordinal)));
    }

    [Test]
    public void EveryLanguage_LoadsFromEmbeddedResource()
    {
        var loc = new Localizer("en");
        foreach (var (code, _) in Localizer.Languages)
        {
            loc.SetLanguage(code);
            foreach (var (key, text) in Table(code))
                Check.Equal(text, loc.T(key));
        }
    }

    static void Compare(Dictionary<string, string> en, string code, List<string> problems)
    {
        var pt = Table(code);
        problems.AddRange(en.Keys.Except(pt.Keys, StringComparer.Ordinal).Select(k => $"missing in {code}: " + k));
        problems.AddRange(pt.Keys.Except(en.Keys, StringComparer.Ordinal).Select(k => $"extra in {code}: " + k));
        foreach (var (key, enText) in en)
        {
            if (!pt.TryGetValue(key, out var ptText)) continue;
            if (string.IsNullOrWhiteSpace(enText) || string.IsNullOrWhiteSpace(ptText)) problems.Add($"empty text ({code}): " + key);
            var a = Placeholders(enText);
            var b = Placeholders(ptText);
            if (!a.SetEquals(b)) problems.Add($"placeholders differ: {key} en={{{string.Join(",", a)}}} {code}={{{string.Join(",", b)}}}");
        }
    }

    [Test]
    public void PartFiles_AreFullyMergedIntoTables()
    {
        foreach (string code in new[] { "en", "pt" })
        {
            var table = Table(code);
            foreach (string part in Directory.EnumerateFiles(Path.Combine(Root, "app", "UnlockGpu.Core", "i18n", "parts"), "*." + code + ".json"))
            {
                var partTable = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(part)) ?? [];
                foreach (var (key, text) in partTable)
                    Check.True(table.TryGetValue(key, out var merged) && merged == text, $"{Path.GetFileName(part)}: {key} not merged into {code}.json");
            }
        }
    }

    [Test]
    public void EmbeddedTables_MatchSourceFiles()
    {
        var en = Table("en");
        var loc = new Localizer("en");
        Check.Equal(en.Count, loc.Keys.Count());
        loc.SetLanguage("pt");
        foreach (var (key, text) in Table("pt"))
            Check.Equal(text, loc.T(key));
    }

    static HashSet<string> Placeholders(string text) =>
        PlaceholderPattern().Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
}
