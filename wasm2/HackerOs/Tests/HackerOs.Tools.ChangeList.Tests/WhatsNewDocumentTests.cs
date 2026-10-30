using System.Text.Json.Nodes;

namespace HackerOs.Tools.ChangeList.Tests;

/// <summary>ADR 0186 §4.1: the <c>whats-new.json</c> the build serves.</summary>
public sealed class WhatsNewDocumentTests
{
    [Fact]
    public void The_next_version_comes_first_then_published_versions_newest_first()
    {
        using ChangeFolderFixture folder = new();
        folder.AddUnreleased("a", "new");
        folder.AddVersion("2.0.3", "2026-10-26", ChangeFolderFixture.Entry("b", "fixed"));
        folder.AddVersion("2.1.0", "2026-10-26", ChangeFolderFixture.Entry("c", "new"));

        JsonObject document = WhatsNewDocument.Build(ChangeFolder.Load(folder.Path), "2.1.0");

        JsonArray groups = document["groups"]!.AsArray();
        Assert.Equal([null, "2.1.0", "2.0.3"], groups.Select(group => group!["version"]?.GetValue<string>()));
        Assert.Equal("2.1.0", groups[0]!["since"]!.GetValue<string>());
        Assert.Equal("2.1.0", document["version"]!.GetValue<string>());
    }

    [Fact]
    public void Only_user_entries_are_served_and_empty_groups_are_left_out()
    {
        using ChangeFolderFixture folder = new();
        folder.AddUnreleased("dev", "developer", audience: "\"developer\"");
        folder.AddUnreleased("both", "improved", audience: "\"user\", \"developer\"");
        folder.AddVersion("2.1.0", "2026-10-26", ChangeFolderFixture.Entry("sdk", "developer", audience: "\"developer\""));

        JsonObject document = WhatsNewDocument.Build(ChangeFolder.Load(folder.Path), "2.1.0");

        JsonNode group = Assert.Single(document["groups"]!.AsArray())!;
        Assert.Equal(["both"], group["entries"]!.AsArray().Select(entry => entry!["id"]!.GetValue<string>()));
    }

    [Fact]
    public void Entries_are_sorted_by_category_then_most_recent_pull_request()
    {
        using ChangeFolderFixture folder = new();
        folder.AddUnreleased("f", "fixed", pr: 300);
        folder.AddUnreleased("n1", "new", pr: 100);
        folder.AddUnreleased("i", "improved", pr: 200);
        folder.AddUnreleased("n2", "new", pr: 150);

        JsonObject document = WhatsNewDocument.Build(ChangeFolder.Load(folder.Path), "2.1.0");

        Assert.Equal(
            ["n2", "n1", "i", "f"],
            document["groups"]![0]!["entries"]!.AsArray().Select(entry => entry!["id"]!.GetValue<string>()));
    }

    [Fact]
    public void Links_and_pull_requests_become_absolute_urls()
    {
        using ChangeFolderFixture folder = new();
        folder.AddUnreleased("a", "new", pr: 42, links: """["doc/en/wasm/features.md", "https://example.com/x"]""");

        JsonObject document = WhatsNewDocument.Build(ChangeFolder.Load(folder.Path), "2.1.0", "https://github.com/o/r/");

        JsonNode entry = document["groups"]![0]!["entries"]![0]!;
        Assert.Equal("https://github.com/o/r/pull/42", entry["prUrl"]!.GetValue<string>());
        Assert.Equal(
            ["https://github.com/o/r/blob/main/doc/en/wasm/features.md", "https://example.com/x"],
            entry["links"]!.AsArray().Select(link => link!.GetValue<string>()));
        Assert.Null(entry["audience"]);
    }

    [Fact]
    public void Writing_the_same_document_twice_keeps_the_file()
    {
        using ChangeFolderFixture folder = new();
        folder.AddUnreleased("a", "new");
        string output = Path.Combine(folder.Path, "out", "whats-new.json");
        JsonObject document = WhatsNewDocument.Build(ChangeFolder.Load(folder.Path), "2.1.0");

        Assert.True(WhatsNewDocument.Write(document, output));
        Assert.False(WhatsNewDocument.Write(document, output));
        Assert.Contains("Titre a", File.ReadAllText(output), StringComparison.Ordinal);
    }

    [Fact]
    public void The_repository_change_list_serves_every_user_entry_once()
    {
        ChangeFolder folder = ChangeFolder.Load(ChangeFolderFixture.RepositoryChanges);
        int userEntries = folder.Unreleased.Concat(folder.Releases.SelectMany(release => release.Entries))
            .Count(entry => ChangeFolder.HasAudience(entry, "user"));

        JsonObject document = WhatsNewDocument.Build(folder, "2.1.0");
        string[] ids = [.. document["groups"]!.AsArray()
            .SelectMany(group => group!["entries"]!.AsArray())
            .Select(entry => entry!["id"]!.GetValue<string>())];

        Assert.NotEmpty(ids);
        Assert.Equal(userEntries, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_command_line_writes_the_file_and_reports_errors()
    {
        using ChangeFolderFixture folder = new();
        folder.AddUnreleased("a", "new");
        string output = Path.Combine(folder.Path, "whats-new.json");

        Assert.Equal(0, Program.Main(["site", "--changes", folder.Path, "--version", "2.1.0", "--output", output]));
        Assert.True(File.Exists(output));
        Assert.Equal(1, Program.Main(["site", "--changes", folder.Path]));
        Assert.Equal(1, Program.Main(["unknown"]));
    }
}
