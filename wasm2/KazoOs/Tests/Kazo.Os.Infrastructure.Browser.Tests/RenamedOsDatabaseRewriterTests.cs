using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kazo.Os.Infrastructure.Browser.Rename;
using Kazo.Os.Infrastructure.Browser.Schema;

namespace Kazo.Os.Infrastructure.Browser.Tests;

/// <summary>The rewrite of the OS database's records written before 3.0.0 (ADR 0230 §3).</summary>
public sealed partial class RenamedOsDatabaseRewriterTests
{
    private const string Root = "00000000000000000000000000000001";
    private const string ConfigText = "# HackerOS apps\n[installed]\norg.hackeros.demo\ntheme=HackerOs\nassembly=HackerOs.Apps.Demo.dll\n[tokens]\n--hos-accent = #ff0066\n";
    private const string DocumentText = "HackerOS notes, my own words.";

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void A_recorded_old_database_keeps_no_old_name_outside_the_documents(int version)
    {
        Dictionary<string, IReadOnlyList<JsonObject>> stores = ReadFixture(version);

        RenamedOsDatabase result = RenamedOsDatabaseRewriter.Rewrite(stores);

        Assert.NotEmpty(result.Changes);
        foreach ((string store, IReadOnlyList<JsonObject> records) in result.Stores)
        {
            foreach (JsonObject record in records)
            {
                Assert.False(OldName().IsMatch(record.ToJsonString()), $"{store}: {record.ToJsonString()}");
            }
        }

        // ~/.config/hackeros is now ~/.config/kazoos; the account and its password did not change.
        Assert.Contains(result.Stores[KazoOsIndexedDbSchema.FileSystemLinkStoreName], link => (string?)link["name"] == "kazoos");
        Assert.Equal(
            stores[KazoOsIndexedDbSchema.UserStoreName].Single().ToJsonString(),
            result.Stores[KazoOsIndexedDbSchema.UserStoreName].Single().ToJsonString());
        Assert.Contains(result.Stores[KazoOsIndexedDbSchema.CatalogStoreName], app => (string?)app["appId"] == "org.kazoos.kazo-paint");
        // The document written by the fixture keeps its words (and so its content hash).
        Assert.Equal(
            stores[KazoOsIndexedDbSchema.FileContentStoreName].Single().ToJsonString(),
            result.Stores[KazoOsIndexedDbSchema.FileContentStoreName].Single().ToJsonString());
    }

    [Fact]
    public void A_catalog_record_whose_app_id_changes_is_deleted_under_its_old_key()
    {
        RenamedOsDatabase result = RenamedOsDatabaseRewriter.Rewrite(ReadFixture(3));

        RenamedRecordChange change = Assert.Single(result.Changes, change =>
            change.StoreName == KazoOsIndexedDbSchema.CatalogStoreName && (string?)change.Put?["appId"] == "org.kazoos.terminal");
        Assert.Equal("org.hackeros.terminal", (string?)change.DeleteKey);
    }

    [Fact]
    public void Configuration_files_are_rewritten_and_stored_under_their_new_hash()
    {
        RenamedOsDatabase result = RenamedOsDatabaseRewriter.Rewrite(SmallDatabase());

        JsonObject config = Entry(result, "config");
        string expected = "# Kazo OS apps\n[installed]\norg.kazoos.demo\ntheme=KazoOs\nassembly=Kazo.Os.Apps.Demo.dll\n[tokens]\n--kos-accent = #ff0066\n";
        Assert.Equal(Hash(expected), (string?)config["contentHash"]);
        Assert.Equal(Encoding.UTF8.GetByteCount(expected), (int?)config["length"]);
        Assert.Equal(expected, Content(result, Hash(expected)));
        // /etc/hackeros became /etc/kazoos: its link was deleted under the old name and written under the new one.
        RenamedRecordChange link = Assert.Single(result.Changes, change =>
            change.StoreName == KazoOsIndexedDbSchema.FileSystemLinkStoreName && (string?)change.Put?["name"] == "kazoos");
        Assert.Equal("[\"etc-id\",\"hackeros\"]", link.DeleteKey!.ToJsonString());
    }

    [Fact]
    public void Documents_keep_their_names_and_words_and_shared_chunks_are_kept()
    {
        RenamedOsDatabase result = RenamedOsDatabaseRewriter.Rewrite(SmallDatabase());

        // ~/Documents/hackeros keeps its name; its file keeps its content.
        Assert.Contains(result.Stores[KazoOsIndexedDbSchema.FileSystemLinkStoreName], link =>
            (string?)link["parentId"] == "documents-id" && (string?)link["name"] == "hackeros");
        Assert.Equal(Hash(DocumentText), (string?)Entry(result, "document")["contentHash"]);
        // The old configuration bytes are also those of ~/Documents/hackeros/copy.cfg: they stay.
        Assert.Equal(Hash(ConfigText), (string?)Entry(result, "copy")["contentHash"]);
        Assert.Equal(ConfigText, Content(result, Hash(ConfigText)));
        Assert.DoesNotContain(result.Changes, change => change.StoreName == KazoOsIndexedDbSchema.FileContentStoreName && change.Put is null);
    }

    [Fact]
    public void Unused_old_chunks_are_deleted()
    {
        Dictionary<string, IReadOnlyList<JsonObject>> stores = SmallDatabase();
        stores[KazoOsIndexedDbSchema.FileSystemEntryStoreName] = [.. stores[KazoOsIndexedDbSchema.FileSystemEntryStoreName].Where(entry => (string?)entry["id"] != "copy")];

        RenamedOsDatabase result = RenamedOsDatabaseRewriter.Rewrite(stores);

        RenamedRecordChange deleted = Assert.Single(result.Changes, change => change.StoreName == KazoOsIndexedDbSchema.FileContentStoreName && change.Put is null);
        Assert.Equal($"[\"{Hash(ConfigText)}\",0]", deleted.DeleteKey!.ToJsonString());
        Assert.DoesNotContain(result.Stores[KazoOsIndexedDbSchema.FileContentStoreName], chunk => (string?)chunk["contentHash"] == Hash(ConfigText));
    }

    [Fact]
    public void Credentials_and_chosen_names_are_never_rewritten()
    {
        Dictionary<string, IReadOnlyList<JsonObject>> stores = SmallDatabase();
        stores[KazoOsIndexedDbSchema.UserStoreName] =
        [
            new JsonObject { ["id"] = "u1", ["loginName"] = "hackeros", ["displayName"] = "HackerOS fan", ["credential"] = new JsonObject { ["salt"] = "HackPaintHackerOs==" } },
        ];

        RenamedOsDatabase result = RenamedOsDatabaseRewriter.Rewrite(stores);

        Assert.Equal(stores[KazoOsIndexedDbSchema.UserStoreName][0].ToJsonString(), result.Stores[KazoOsIndexedDbSchema.UserStoreName][0].ToJsonString());
    }

    [Fact]
    public void Rewriting_twice_changes_nothing_the_second_time()
    {
        RenamedOsDatabase first = RenamedOsDatabaseRewriter.Rewrite(SmallDatabase());

        RenamedOsDatabase second = RenamedOsDatabaseRewriter.Rewrite(first.Stores);

        Assert.Empty(second.Changes);
    }

    [Theory]
    [InlineData("/etc/hackeros/apps.config", "/etc/kazoos/apps.config")]
    [InlineData("/home/ada/.config/hackeros/x", "/home/ada/.config/kazoos/x")]
    [InlineData("/var/log/hackweb/access.log", "/var/log/kazoweb/access.log")]
    [InlineData("/home/ada/.hackeros/aliases", "/home/ada/.kazoos/aliases")]
    [InlineData("/usr/lib/hackeros/apps", "/usr/lib/kazoos/apps")]
    [InlineData("/home/ada/Documents/hackeros/x", "/home/ada/Documents/hackeros/x")]
    [InlineData("/tmp/hackeros", "/tmp/hackeros")]
    [InlineData("hackeros/relative", "hackeros/relative")]
    public void System_paths_are_renamed_and_document_paths_are_not(string before, string after) =>
        Assert.Equal(after, RenamedOsDatabaseRewriter.RewriteSystemPath(before));

    /// <summary>
    /// <c>/etc/hackeros/apps.config</c>, <c>~/Documents/hackeros/note.txt</c> and
    /// <c>~/Documents/hackeros/copy.cfg</c> (same bytes as the configuration file).
    /// </summary>
    private static Dictionary<string, IReadOnlyList<JsonObject>> SmallDatabase() => new(StringComparer.Ordinal)
    {
        [KazoOsIndexedDbSchema.FileSystemLinkStoreName] =
        [
            Link(Root, "etc", "etc-id"),
            Link("etc-id", "hackeros", "etc-hackeros-id"),
            Link("etc-hackeros-id", "apps.config", "config"),
            Link(Root, "home", "home-id"),
            Link("home-id", "ada", "ada-id"),
            Link("ada-id", "Documents", "documents-id"),
            Link("documents-id", "hackeros", "documents-hackeros-id"),
            Link("documents-hackeros-id", "note.txt", "document"),
            Link("documents-hackeros-id", "copy.cfg", "copy"),
        ],
        [KazoOsIndexedDbSchema.FileSystemEntryStoreName] =
        [
            File("config", ConfigText),
            File("document", DocumentText),
            File("copy", ConfigText),
        ],
        [KazoOsIndexedDbSchema.FileContentStoreName] = [Chunk(ConfigText), Chunk(DocumentText)],
    };

    private static JsonObject Link(string parent, string name, string entry) =>
        new() { ["parentId"] = parent, ["name"] = name, ["entryId"] = entry };

    private static JsonObject File(string id, string text) => new()
    {
        ["id"] = id, ["kind"] = 0, ["ownerId"] = "system", ["contentHash"] = Hash(text),
        ["length"] = Encoding.UTF8.GetByteCount(text), ["symbolicLinkTarget"] = null,
    };

    private static JsonObject Chunk(string text) => new()
    {
        ["contentHash"] = Hash(text), ["chunkIndex"] = 0,
        ["dataBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), ["createdUtcMs"] = 1,
    };

    private static JsonObject Entry(RenamedOsDatabase database, string id) =>
        database.Stores[KazoOsIndexedDbSchema.FileSystemEntryStoreName].Single(entry => (string?)entry["id"] == id);

    private static string Content(RenamedOsDatabase database, string hash) => Encoding.UTF8.GetString(Convert.FromBase64String(
        (string)database.Stores[KazoOsIndexedDbSchema.FileContentStoreName].Single(chunk => (string?)chunk["contentHash"] == hash)["dataBase64"]!));

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static Dictionary<string, IReadOnlyList<JsonObject>> ReadFixture(int version)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(Path.Combine(directory.FullName, "Kazo.Os.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        JsonObject fixture = JsonNode.Parse(System.IO.File.ReadAllText(
            Path.Combine(directory.FullName, "Tests", "fixtures", "indexeddb", $"hackeros-v{version}.json")))!.AsObject();
        return fixture["objectStores"]!.AsArray().ToDictionary(
            store => (string)store!["name"]!,
            store => (IReadOnlyList<JsonObject>)[.. store!["records"]!.AsArray().Select(record => record!.AsObject().DeepClone().AsObject())],
            StringComparer.Ordinal);
    }

    /// <summary>Any old name as the end-to-end test looks for it.</summary>
    [GeneratedRegex("hacker ?os|hackweb|hack-?paint", RegexOptions.IgnoreCase)]
    internal static partial Regex OldName();
}
