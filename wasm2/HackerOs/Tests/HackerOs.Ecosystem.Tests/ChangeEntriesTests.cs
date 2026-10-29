using System.Text.Json;
using System.Xml.Linq;

namespace HackerOs.Ecosystem.Tests;

/// <summary>
/// ADR 0186 section 2: every entry of the change list (<c>changes/unreleased/&lt;guid&gt;.json</c> and
/// <c>changes/&lt;version&gt;.json</c>) has a valid GUID that is unique across the whole folder, a
/// known category and audience, an English and a French text, and links that exist in the repository.
/// </summary>
public sealed class ChangeEntriesTests
{
    private static readonly string Root = FindRoot();
    private static readonly string ChangesDirectory = Path.Combine(Root, "changes");
    private static readonly string UnreleasedDirectory = Path.Combine(ChangesDirectory, "unreleased");
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(Root, "..", ".."));

    private static readonly HashSet<string> Categories = ["new", "improved", "fixed", "developer"];
    private static readonly HashSet<string> Audiences = ["user", "developer"];
    private static readonly HashSet<string> EntryProperties = ["id", "category", "audience", "title", "description", "pr", "links"];
    private static readonly HashSet<string> VersionProperties = ["version", "date", "entries"];
    private static readonly HashSet<string> Languages = ["en", "fr"];

    public static TheoryData<string> UnreleasedFiles()
    {
        TheoryData<string> data = [];
        foreach (string file in Directory.EnumerateFiles(UnreleasedDirectory).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    public static TheoryData<string> VersionFiles()
    {
        TheoryData<string> data = [];
        foreach (string file in Directory.EnumerateFiles(ChangesDirectory).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Fact]
    public void The_change_list_has_entries() =>
        Assert.NotEmpty(AllEntries());

    [Theory]
    [MemberData(nameof(UnreleasedFiles))]
    public void Every_unreleased_entry_is_named_after_its_id(string fileName)
    {
        Assert.EndsWith(".json", fileName, StringComparison.Ordinal);
        using JsonDocument document = Parse(Path.Combine(UnreleasedDirectory, fileName));

        List<string> errors = ValidateEntry(document.RootElement);
        string id = document.RootElement.TryGetProperty("id", out JsonElement value) ? value.GetString() ?? "" : "";
        if (!string.Equals(fileName, id + ".json", StringComparison.Ordinal))
        {
            errors.Add($"the file name must be the id: {id}.json");
        }

        Assert.Empty(errors);
    }

    [Theory]
    [MemberData(nameof(VersionFiles))]
    public void Every_version_file_is_named_after_its_version_and_holds_valid_entries(string fileName)
    {
        using JsonDocument document = Parse(Path.Combine(ChangesDirectory, fileName));
        JsonElement root = document.RootElement;
        List<string> errors = [];

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        errors.AddRange(UnknownProperties(root, VersionProperties));
        string version = root.TryGetProperty("version", out JsonElement versionValue) && versionValue.ValueKind == JsonValueKind.String
            ? versionValue.GetString()!
            : "";
        if (!Version.TryParse(version, out Version? parsed) || parsed.Build < 0 || parsed.Revision >= 0)
        {
            errors.Add($"version must be major.minor.patch: '{version}'");
        }

        if (!string.Equals(fileName, version + ".json", StringComparison.Ordinal))
        {
            errors.Add($"the file name must be the version: {version}.json");
        }

        if (!root.TryGetProperty("date", out JsonElement date) || date.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(date.GetString(), "yyyy-MM-dd", out _))
        {
            errors.Add("date must be yyyy-MM-dd");
        }

        if (!root.TryGetProperty("entries", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
        {
            errors.Add("entries must be an array");
        }
        else
        {
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                errors.AddRange(ValidateEntry(entry));
            }
        }

        Assert.Empty(errors);
    }

    [Fact]
    public void Every_id_is_unique_across_the_change_list()
    {
        string[] duplicates = [.. AllEntries()
            .GroupBy(static entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => $"{group.Key}: {string.Join(", ", group.Select(static entry => entry.Source))}")];

        Assert.Empty(duplicates);
    }

    [Fact]
    public void No_version_file_is_newer_than_the_current_version()
    {
        Version current = CurrentVersion();
        string[] newer = [.. Directory.EnumerateFiles(ChangesDirectory, "*.json")
            .Select(static file => Path.GetFileNameWithoutExtension(file))
            .Where(name => Version.TryParse(name, out Version? version) && version > current)];

        Assert.Empty(newer);
    }

    private static List<string> ValidateEntry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return ["an entry must be an object"];
        }

        string id = entry.TryGetProperty("id", out JsonElement idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString()! : "";
        List<string> errors = [.. UnknownProperties(entry, EntryProperties).Select(error => $"{id}: {error}")];
        void Error(string message) => errors.Add($"{(id.Length == 0 ? "(no id)" : id)}: {message}");

        if (!Guid.TryParseExact(id, "D", out Guid guid) || guid == Guid.Empty || !string.Equals(id, guid.ToString("D"), StringComparison.Ordinal))
        {
            Error("id must be a lowercase GUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)");
        }

        string category = entry.TryGetProperty("category", out JsonElement categoryValue) && categoryValue.ValueKind == JsonValueKind.String
            ? categoryValue.GetString()!
            : "";
        if (!Categories.Contains(category))
        {
            Error($"category must be one of {string.Join(", ", Categories)}");
        }

        string[] audience = entry.TryGetProperty("audience", out JsonElement audienceValue) && audienceValue.ValueKind == JsonValueKind.Array
            ? [.. audienceValue.EnumerateArray().Select(static item => item.ValueKind == JsonValueKind.String ? item.GetString()! : "")]
            : [];
        if (audience.Length == 0 || audience.Any(item => !Audiences.Contains(item)) || audience.Distinct(StringComparer.Ordinal).Count() != audience.Length)
        {
            Error("audience must list user and/or developer, each once");
        }

        if (category == "developer" && !audience.SequenceEqual(["developer"]))
        {
            Error("a developer entry has the developer audience only");
        }

        ValidateText(entry, "title", required: true, Error);
        ValidateText(entry, "description", required: false, Error);

        if (entry.TryGetProperty("pr", out JsonElement pr) && (pr.ValueKind != JsonValueKind.Number || !pr.TryGetInt32(out int number) || number <= 0))
        {
            Error("pr must be a positive pull request number");
        }

        if (entry.TryGetProperty("links", out JsonElement links))
        {
            if (links.ValueKind != JsonValueKind.Array)
            {
                Error("links must be an array");
            }
            else
            {
                foreach (JsonElement link in links.EnumerateArray())
                {
                    string target = link.ValueKind == JsonValueKind.String ? link.GetString()! : "";
                    if (!IsValidLink(target))
                    {
                        Error($"link is neither an https URL nor a file of the repository: '{target}'");
                    }
                }
            }
        }

        return errors;
    }

    private static void ValidateText(JsonElement entry, string name, bool required, Action<string> error)
    {
        if (!entry.TryGetProperty(name, out JsonElement text))
        {
            if (required)
            {
                error($"{name} is required");
            }

            return;
        }

        if (text.ValueKind != JsonValueKind.Object)
        {
            error($"{name} must be an object with en and fr");
            return;
        }

        foreach (string unknown in UnknownProperties(text, Languages))
        {
            error($"{name}: {unknown}");
        }

        foreach (string language in Languages)
        {
            if (!text.TryGetProperty(language, out JsonElement value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                error($"{name}.{language} is required and not empty");
            }
        }
    }

    private static bool IsValidLink(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? uri))
        {
            return uri.Scheme == Uri.UriSchemeHttps;
        }

        if (target.Length == 0 || Path.IsPathRooted(target) || target.Contains("..", StringComparison.Ordinal) || target.Contains('\\'))
        {
            return false;
        }

        return File.Exists(Path.Combine(RepositoryRoot, target));
    }

    private static IEnumerable<string> UnknownProperties(JsonElement element, HashSet<string> known) =>
        element.EnumerateObject().Where(property => !known.Contains(property.Name)).Select(static property => $"unknown property '{property.Name}'");

    private static List<(string Id, string Source)> AllEntries()
    {
        List<(string Id, string Source)> entries = [];
        foreach (string file in Directory.EnumerateFiles(UnreleasedDirectory, "*.json"))
        {
            using JsonDocument document = Parse(file);
            entries.Add((document.RootElement.GetProperty("id").GetString()!, "unreleased/" + Path.GetFileName(file)));
        }

        foreach (string file in Directory.EnumerateFiles(ChangesDirectory, "*.json"))
        {
            using JsonDocument document = Parse(file);
            foreach (JsonElement entry in document.RootElement.GetProperty("entries").EnumerateArray())
            {
                entries.Add((entry.GetProperty("id").GetString()!, Path.GetFileName(file)));
            }
        }

        return entries;
    }

    private static JsonDocument Parse(string path) =>
        JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });

    private static Version CurrentVersion()
    {
        string text = XDocument.Load(Path.Combine(Root, "build", "Versions.props"))
            .Descendants("HackerOsVersion")
            .Single()
            .Value;
        return Version.Parse(text.Split('-')[0]);
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HackerOs.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("HackerOs.sln not found above the test output directory.");
    }
}
