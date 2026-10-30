using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HackerOs.Tools.ChangeList;

/// <summary>
/// Builds the <c>/whats-new.json</c> the OS reads (ADR 0186 §4.1): the <c>user</c> entries of every
/// published version and of the next one, grouped by version, the most recent group first.
/// </summary>
/// <remarks>
/// <code>
/// {
///   "product": "HackerOS",
///   "version": "2.1.0",
///   "groups": [
///     { "version": null, "since": "2.1.0", "entries": [ … ] },
///     { "version": "2.1.0", "date": "2026-10-26", "entries": [ … ] }
///   ]
/// }
/// </code>
/// An entry keeps <c>id</c>, <c>category</c>, <c>title</c>, <c>description</c> and <c>pr</c>; its
/// <c>links</c> become absolute URLs (a repository path points to the file on GitHub) and its pull
/// request gets a <c>prUrl</c>. Groups without a <c>user</c> entry are left out.
/// </remarks>
internal static class WhatsNewDocument
{
    /// <summary>The repository the links of the entries point to.</summary>
    public const string DefaultRepositoryUrl = "https://github.com/Kazo-ca/HackerOs";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        // Accented French text stays readable; the file is served as JSON, never inlined in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Builds the document.</summary>
    /// <param name="folder">The change list.</param>
    /// <param name="version">The version being built (<c>HackerOsVersion</c>): the next group says "since" it.</param>
    /// <param name="repositoryUrl">The GitHub repository that repository paths and pull requests point to.</param>
    public static JsonObject Build(ChangeFolder folder, string version, string repositoryUrl = DefaultRepositoryUrl)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        string repository = repositoryUrl.TrimEnd('/');

        JsonArray groups = [];
        JsonArray next = UserEntries(folder.Unreleased, repository);
        if (next.Count > 0)
        {
            groups.Add(new JsonObject { ["version"] = null, ["since"] = version, ["entries"] = next });
        }

        foreach (ChangeRelease release in folder.Releases)
        {
            JsonArray entries = UserEntries(release.Entries, repository);
            if (entries.Count > 0)
            {
                groups.Add(new JsonObject
                {
                    ["version"] = release.Version.ToString(),
                    ["date"] = release.Date,
                    ["entries"] = entries
                });
            }
        }

        return new JsonObject { ["product"] = "HackerOS", ["version"] = version, ["groups"] = groups };
    }

    /// <summary>Writes the document to <paramref name="output"/>, only when its content changed (incremental builds).</summary>
    /// <returns><see langword="true"/> when the file was written.</returns>
    public static bool Write(JsonObject document, string output)
    {
        string text = document.ToJsonString(Indented) + "\n";
        if (File.Exists(output) && string.Equals(File.ReadAllText(output), text, StringComparison.Ordinal))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, text);
        return true;
    }

    private static JsonArray UserEntries(IEnumerable<JsonObject> entries, string repository)
    {
        JsonArray result = [];
        foreach (JsonObject entry in entries.Where(entry => ChangeFolder.HasAudience(entry, "user")))
        {
            JsonObject item = new()
            {
                ["id"] = entry["id"]?.DeepClone(),
                ["category"] = entry["category"]?.DeepClone(),
                ["title"] = entry["title"]?.DeepClone()
            };
            if (entry["description"] is JsonObject description)
            {
                item["description"] = description.DeepClone();
            }

            if (entry["pr"] is JsonValue pr && pr.TryGetValue(out int number))
            {
                item["pr"] = number;
                item["prUrl"] = $"{repository}/pull/{number}";
            }

            JsonArray links = [.. (entry["links"] as JsonArray ?? [])
                .Select(link => link?.GetValue<string>())
                .Where(link => !string.IsNullOrWhiteSpace(link))
                .Select(link => (JsonNode?)JsonValue.Create(ToUrl(link!, repository)))];
            if (links.Count > 0)
            {
                item["links"] = links;
            }

            result.Add(item);
        }

        return result;
    }

    /// <summary>An <c>https</c> link stays as is; a repository path points to the file on the main branch.</summary>
    internal static string ToUrl(string link, string repository) =>
        link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? link
            : $"{repository}/blob/main/{link.TrimStart('/')}";
}
