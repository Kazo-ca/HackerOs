using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Kazo.Os.Ecosystem.Tests;

/// <summary>
/// ADR 0104 section 6: every English <c>.resx</c> of the solution has a French twin with exactly
/// the same keys and the same format placeholders, and no text is empty. The manifest sources
/// (<c>AppManifest.&lt;culture&gt;.resx</c>) are checked by the generator instead (<c>HOSAPP0040</c>).
/// </summary>
public sealed partial class ResourceParityTests
{
    private static readonly string Root = FindRoot();

    public static TheoryData<string> NeutralFiles()
    {
        TheoryData<string> data = [];
        foreach (string file in EnumerateResx().Where(static file => !IsCultureFile(file)))
        {
            data.Add(Path.GetRelativePath(Root, file).Replace('\\', '/'));
        }

        return data;
    }

    [Fact]
    public void The_solution_has_translated_resources() =>
        Assert.NotEmpty(NeutralFiles());

    [Theory]
    [MemberData(nameof(NeutralFiles))]
    public void Every_English_resource_has_a_French_twin_with_the_same_keys_and_placeholders(string relativePath)
    {
        string english = Path.Combine(Root, relativePath);
        string french = Path.ChangeExtension(english, ".fr.resx");
        Assert.True(File.Exists(french), $"{relativePath} has no .fr.resx");

        Dictionary<string, string> en = Read(english);
        Dictionary<string, string> fr = Read(french);

        Assert.Empty(en.Keys.Except(fr.Keys).Select(key => $"missing in French: {key}"));
        Assert.Empty(fr.Keys.Except(en.Keys).Select(key => $"only in French: {key}"));
        Assert.Empty(en.Concat(fr).Where(static pair => string.IsNullOrWhiteSpace(pair.Value)).Select(static pair => $"empty: {pair.Key}"));
        Assert.Empty(en.Keys
            .Where(key => !Placeholders(en[key]).SetEquals(Placeholders(fr[key])))
            .Select(key => $"placeholders differ: {key}"));
    }

    [Fact]
    public void Every_French_resource_has_an_English_original()
    {
        string[] orphans = [.. EnumerateResx()
            .Where(static file => file.EndsWith(".fr.resx", StringComparison.Ordinal) && !Path.GetFileName(file).StartsWith("AppManifest.", StringComparison.Ordinal))
            .Where(static file => !File.Exists(file[..^".fr.resx".Length] + ".resx"))];

        Assert.Empty(orphans);
    }

    private static IEnumerable<string> EnumerateResx()
    {
        foreach (string top in new[] { "Apps", "Platform", "Shared", "OS", "Infrastructure", "Server", "Samples" })
        {
            string directory = Path.Combine(Root, top);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*.resx", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
                if (!relative.Contains("/bin/", StringComparison.Ordinal)
                    && !relative.Contains("/obj/", StringComparison.Ordinal)
                    && !Path.GetFileName(file).StartsWith("AppManifest.", StringComparison.Ordinal))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>A culture file is <c>Name.&lt;culture&gt;.resx</c>; English files have no culture suffix.</summary>
    private static bool IsCultureFile(string file) =>
        Path.GetFileNameWithoutExtension(file).Contains('.', StringComparison.Ordinal);

    private static Dictionary<string, string> Read(string file) =>
        XDocument.Load(file).Root!.Elements("data")
            .ToDictionary(
                static data => (string)data.Attribute("name")!,
                static data => (string?)data.Element("value") ?? string.Empty,
                StringComparer.Ordinal);

    private static HashSet<string> Placeholders(string text) =>
        [.. PlaceholderPattern().Matches(text.Replace("{{", string.Empty, StringComparison.Ordinal)).Select(static match => match.Groups[1].Value)];

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex PlaceholderPattern();

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Kazo.Os.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Kazo.Os.sln not found above the test output directory.");
    }
}
