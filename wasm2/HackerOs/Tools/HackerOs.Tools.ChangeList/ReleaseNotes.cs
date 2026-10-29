using System.Text;
using System.Text.Json.Nodes;

namespace HackerOs.Tools.ChangeList;

/// <summary>
/// The texts written from the change list when a version is published (ADR 0186 §7): the GitHub
/// Release (Markdown, English, every entry), the <c>PackageReleaseNotes</c> of the NuGet packages
/// (plain text, English, <c>developer</c> entries) and <c>CHANGELOG.md</c> / <c>CHANGELOG-fr.md</c>
/// (Markdown, every published version).
/// </summary>
internal static class ReleaseNotes
{
    /// <summary>The English changelog, at the root of the repository.</summary>
    public const string EnglishChangelog = "CHANGELOG.md";

    /// <summary>The French changelog, at the root of the repository.</summary>
    public const string FrenchChangelog = "CHANGELOG-fr.md";

    private static readonly IReadOnlyDictionary<string, (string En, string Fr)> CategoryTitles =
        new Dictionary<string, (string En, string Fr)>(StringComparer.Ordinal)
        {
            ["new"] = ("New", "Nouveau"),
            ["improved"] = ("Improved", "Amélioré"),
            ["fixed"] = ("Fixed", "Corrigé"),
            ["developer"] = ("For developers", "Pour les développeurs")
        };

    /// <summary>
    /// The entries of <paramref name="version"/>: its <c>changes/&lt;version&gt;.json</c> once it is
    /// collected, otherwise the next version (<c>unreleased/</c>), so a local pack before the release
    /// already carries its notes.
    /// </summary>
    public static IReadOnlyList<JsonObject> EntriesOf(ChangeFolder folder, string version)
    {
        ArgumentNullException.ThrowIfNull(folder);
        Version parsed = Version.TryParse(version.Split('-')[0], out Version? number)
            ? number
            : throw new ArgumentException($"Invalid version: {version}");
        return folder.Releases.FirstOrDefault(release => release.Version == parsed)?.Entries ?? folder.Unreleased;
    }

    /// <summary>The body of the GitHub Release: every entry, grouped by category.</summary>
    public static string Release(ChangeFolder folder, string version, string repositoryUrl = WhatsNewDocument.DefaultRepositoryUrl)
    {
        string repository = repositoryUrl.TrimEnd('/');
        IReadOnlyList<JsonObject> entries = EntriesOf(folder, version);
        StringBuilder text = new();
        if (entries.Count == 0)
        {
            text.Append("No change listed for this version.\n");
        }
        else
        {
            AppendCategories(text, entries, "en", link => WhatsNewDocument.ToUrl(link, repository), repository);
        }

        text.Append($"\nAll versions: [CHANGELOG.md]({repository}/blob/main/{EnglishChangelog})\n");
        return text.ToString();
    }

    /// <summary>The <c>PackageReleaseNotes</c> of every <c>HackerOs.*</c> package: the <c>developer</c> entries and the Release link.</summary>
    public static string Package(ChangeFolder folder, string version, string repositoryUrl = WhatsNewDocument.DefaultRepositoryUrl)
    {
        string repository = repositoryUrl.TrimEnd('/');
        List<JsonObject> entries = [.. EntriesOf(folder, version).Where(entry => ChangeFolder.HasAudience(entry, "developer"))];
        StringBuilder text = new();
        text.Append(entries.Count == 0
            ? $"HackerOS {version}: no change for app developers.\n"
            : $"HackerOS {version}, changes for app developers:\n");
        foreach (JsonObject entry in entries)
        {
            text.Append("- ").Append(Sentence(ChangeFolder.Text(entry, "title", "en")));
            string description = ChangeFolder.Text(entry, "description", "en");
            if (description.Length > 0)
            {
                text.Append(' ').Append(description);
            }

            if (PullRequest(entry) is { } number)
            {
                text.Append($" (#{number})");
            }

            text.Append('\n');
        }

        text.Append($"Release notes: {repository}/releases/tag/v{version}\n");
        return text.ToString();
    }

    /// <summary>
    /// <c>CHANGELOG.md</c> (<paramref name="language"/> <c>en</c>) or <c>CHANGELOG-fr.md</c> (<c>fr</c>):
    /// every published version, the most recent first. The next version stays out: it would make
    /// every pull request rewrite the same file (ADR 0186 §1).
    /// </summary>
    public static string Changelog(ChangeFolder folder, string language)
    {
        ArgumentNullException.ThrowIfNull(folder);
        bool french = language == "fr";
        StringBuilder text = new();
        text.Append(french ? $"> [English version]({EnglishChangelog})\n\n" : $"> [Version française]({FrenchChangelog})\n\n");
        text.Append(french ? "# Journal des changements\n\n" : "# Changelog\n\n");
        text.Append(french
            ? "Les changements de HackerOS, par version. Ce fichier est généré à partir de `wasm2/HackerOs/changes/` par\n" +
              "`wasm2/HackerOs/build/tools/collect-changes.sh` (ADR 0186) : ne pas le modifier à la main. Les changements\n" +
              "déjà déployés mais pas encore publiés sont dans la page Paramètres ▸ Nouveautés de l'OS.\n"
            : "The changes of HackerOS, by version. This file is generated from `wasm2/HackerOs/changes/` by\n" +
              "`wasm2/HackerOs/build/tools/collect-changes.sh` (ADR 0186): do not edit it by hand. Changes already\n" +
              "deployed but not released yet are in the Settings ▸ What's new page of the OS.\n");

        foreach (ChangeRelease release in folder.Releases)
        {
            text.Append($"\n## {release.Version}");
            if (release.Date.Length > 0)
            {
                text.Append($" ({release.Date})");
            }

            text.Append("\n\n");
            if (release.Entries.Count == 0)
            {
                text.Append(french ? "Aucun changement listé.\n" : "No change listed.\n");
                continue;
            }

            // Repository paths stay relative (the changelog sits at the root of the repository); the
            // French changelog points to the French doc, which has the same paths under doc/fr/.
            AppendCategories(
                text,
                release.Entries,
                language,
                link => french && link.StartsWith("doc/en/", StringComparison.Ordinal) ? "doc/fr/" + link["doc/en/".Length..] : link,
                WhatsNewDocument.DefaultRepositoryUrl);
        }

        return text.ToString();
    }

    /// <summary>Writes <paramref name="text"/> to <paramref name="output"/>, only when it changed.</summary>
    /// <returns><see langword="true"/> when the file was written.</returns>
    public static bool Write(string text, string output)
    {
        if (File.Exists(output) && string.Equals(File.ReadAllText(output), text, StringComparison.Ordinal))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, text);
        return true;
    }

    private static void AppendCategories(
        StringBuilder text,
        IReadOnlyList<JsonObject> entries,
        string language,
        Func<string, string> linkTarget,
        string repository)
    {
        bool first = true;
        foreach (IGrouping<string, JsonObject> group in entries.GroupBy(entry => entry["category"]?.GetValue<string>() ?? ""))
        {
            string title = CategoryTitles.TryGetValue(group.Key, out (string En, string Fr) titles)
                ? (language == "fr" ? titles.Fr : titles.En)
                : group.Key;
            text.Append(first ? "" : "\n").Append($"### {title}\n\n");
            first = false;
            foreach (JsonObject entry in group)
            {
                AppendEntry(text, entry, language, linkTarget, repository);
            }
        }
    }

    private static void AppendEntry(StringBuilder text, JsonObject entry, string language, Func<string, string> linkTarget, string repository)
    {
        text.Append("- **").Append(Sentence(Localized(entry, "title", language))).Append("**");
        string description = Localized(entry, "description", language);
        if (description.Length > 0)
        {
            text.Append(' ').Append(description);
        }

        List<string> references = [];
        if (PullRequest(entry) is { } number)
        {
            references.Add($"[#{number}]({repository}/pull/{number})");
        }

        foreach (string link in (entry["links"] as JsonArray ?? [])
            .Select(link => link?.GetValue<string>())
            .Where(link => !string.IsNullOrWhiteSpace(link))
            .Cast<string>())
        {
            references.Add($"[{LinkLabel(link)}]({linkTarget(link)})");
        }

        if (references.Count > 0)
        {
            text.Append(" (").Append(string.Join(", ", references)).Append(')');
        }

        text.Append('\n');
    }

    /// <summary>The text in <paramref name="language"/>, or the English one (ADR 0186 §2).</summary>
    private static string Localized(JsonObject entry, string property, string language)
    {
        string text = ChangeFolder.Text(entry, property, language);
        return text.Length > 0 ? text : ChangeFolder.Text(entry, property, "en");
    }

    /// <summary>A title becomes a sentence: it ends with a period unless it already ends with punctuation.</summary>
    private static string Sentence(string title) =>
        title.Length == 0 || title[^1] is '.' or '!' or '?' or ')' ? title : title + ".";

    private static int? PullRequest(JsonObject entry) =>
        entry["pr"] is JsonValue pr && pr.TryGetValue(out int number) ? number : null;

    /// <summary>A repository path is shown by its file name; an <c>https</c> link by its host and path.</summary>
    private static string LinkLabel(string link) =>
        link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(link, UriKind.Absolute, out Uri? uri)
            ? (uri.Host + uri.AbsolutePath).TrimEnd('/')
            : Path.GetFileName(link.TrimEnd('/'));
}
