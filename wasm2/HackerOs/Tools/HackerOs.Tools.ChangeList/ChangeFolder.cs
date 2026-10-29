using System.Text.Json;
using System.Text.Json.Nodes;

namespace HackerOs.Tools.ChangeList;

/// <summary>One published version of the change list: <c>changes/&lt;version&gt;.json</c> (ADR 0186 §1).</summary>
/// <param name="Version">The version number (three parts).</param>
/// <param name="Date">The release date, <c>yyyy-MM-dd</c>.</param>
/// <param name="Entries">Its entries, in display order (<see cref="ChangeFolder.Sort"/>).</param>
internal sealed record ChangeRelease(Version Version, string Date, IReadOnlyList<JsonObject> Entries);

/// <summary>
/// The <c>changes/</c> folder (ADR 0186 §1): the entries of the next version in
/// <c>unreleased/&lt;id&gt;.json</c> and one file per published version. The schema itself is checked
/// by <c>ChangeEntriesTests</c>; this reader only needs the fields it uses.
/// </summary>
internal sealed class ChangeFolder
{
    /// <summary>Categories in display order (ADR 0186 §2).</summary>
    public static readonly IReadOnlyList<string> Categories = ["new", "improved", "fixed", "developer"];

    private ChangeFolder(string path, IReadOnlyList<ChangeRelease> releases, IReadOnlyList<JsonObject> unreleased)
    {
        Path = path;
        Releases = releases;
        Unreleased = unreleased;
    }

    /// <summary>Gets the full path of the folder.</summary>
    public string Path { get; }

    /// <summary>Gets the published versions, the most recent first.</summary>
    public IReadOnlyList<ChangeRelease> Releases { get; }

    /// <summary>Gets the entries of the next version (<c>unreleased/</c>), in display order.</summary>
    public IReadOnlyList<JsonObject> Unreleased { get; }

    /// <summary>Reads a <c>changes/</c> folder; a missing folder is an empty list.</summary>
    /// <param name="path">The folder.</param>
    /// <exception cref="InvalidDataException">A file is not a JSON object or a version file has no valid version.</exception>
    public static ChangeFolder Load(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        List<ChangeRelease> releases = [];
        List<JsonObject> unreleased = [];
        if (Directory.Exists(full))
        {
            foreach (string file in Directory.EnumerateFiles(full, "*.json"))
            {
                JsonObject root = ReadObject(file);
                string? text = root["version"]?.GetValue<string>();
                if (!Version.TryParse(text, out Version? version))
                {
                    throw new InvalidDataException($"{file}: \"version\" is missing or invalid.");
                }

                IEnumerable<JsonObject> entries = (root["entries"] as JsonArray ?? []).OfType<JsonObject>().Select(Detach);
                releases.Add(new ChangeRelease(version, root["date"]?.GetValue<string>() ?? "", Sort(entries)));
            }

            string unreleasedPath = System.IO.Path.Combine(full, "unreleased");
            if (Directory.Exists(unreleasedPath))
            {
                unreleased.AddRange(Directory.EnumerateFiles(unreleasedPath, "*.json").Select(ReadObject));
            }
        }

        return new ChangeFolder(full, [.. releases.OrderByDescending(release => release.Version)], Sort(unreleased));
    }

    /// <summary>
    /// Display order of entries: by category (<see cref="Categories"/>), then the most recent pull
    /// request first, then by English title. The order of the files on disk never matters.
    /// </summary>
    public static IReadOnlyList<JsonObject> Sort(IEnumerable<JsonObject> entries) =>
        [.. entries
            .OrderBy(entry => CategoryRank(entry["category"]?.GetValue<string>()))
            .ThenByDescending(entry => entry["pr"]?.GetValue<int>() ?? 0)
            .ThenBy(entry => Text(entry, "title", "en"), StringComparer.Ordinal)
            .ThenBy(entry => entry["id"]?.GetValue<string>(), StringComparer.Ordinal)];

    /// <summary>Whether the entry's <c>audience</c> holds <paramref name="audience"/>.</summary>
    public static bool HasAudience(JsonObject entry, string audience) =>
        (entry["audience"] as JsonArray ?? []).Any(value => value?.GetValue<string>() == audience);

    /// <summary>The text of <paramref name="property"/> (<c>title</c> or <c>description</c>) in <paramref name="language"/>, or empty.</summary>
    public static string Text(JsonObject entry, string property, string language) =>
        (entry[property] as JsonObject)?[language]?.GetValue<string>() ?? "";

    private static int CategoryRank(string? category)
    {
        int index = category is null ? -1 : Categories.ToList().IndexOf(category);
        return index < 0 ? Categories.Count : index;
    }

    private static JsonObject Detach(JsonObject entry) => (JsonObject)entry.DeepClone();

    private static JsonObject ReadObject(string file)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(file)) as JsonObject
                ?? throw new InvalidDataException($"{file}: the file must hold a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{file}: {exception.Message}", exception);
        }
    }
}
