using System.Text.Json.Nodes;

namespace HackerOs.Tools.ChangeList.Tests;

/// <summary>ADR 0186 §1: <c>collect-changes.sh</c> moves the next version into its version file.</summary>
public sealed class ChangeCollectorTests
{
    [Fact]
    public void Unreleased_entries_move_unchanged_into_the_version_file_in_display_order()
    {
        using ChangeFolderFixture folder = new();
        folder.AddUnreleased("f", "fixed", pr: 3);
        folder.AddUnreleased("n", "new", pr: 2);

        string output = ChangeCollector.Collect(folder.Path, "2.2.0", "2026-11-01");

        JsonObject document = JsonNode.Parse(File.ReadAllText(output))!.AsObject();
        Assert.Equal("2.2.0", document["version"]!.GetValue<string>());
        Assert.Equal("2026-11-01", document["date"]!.GetValue<string>());
        Assert.Equal(["n", "f"], document["entries"]!.AsArray().Select(entry => entry!["id"]!.GetValue<string>()));
        Assert.Equal("Titre n", document["entries"]![0]!["title"]!["fr"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(Path.Combine(folder.Path, "unreleased")));
    }

    [Fact]
    public void A_collected_version_shows_its_entries_under_its_number_with_the_same_ids()
    {
        using ChangeFolderFixture folder = new();
        folder.AddUnreleased("a", "new");

        ChangeCollector.Collect(folder.Path, "2.2.0", "2026-11-01");
        ChangeFolder changes = ChangeFolder.Load(folder.Path);

        ChangeRelease release = Assert.Single(changes.Releases);
        Assert.Equal(new Version(2, 2, 0), release.Version);
        Assert.Equal("a", Assert.Single(release.Entries)["id"]!.GetValue<string>());
        Assert.Empty(changes.Unreleased);
    }

    [Theory]
    [InlineData("2.2", "2026-11-01")]
    [InlineData("2.2.0", "01/11/2026")]
    public void Invalid_version_or_date_is_refused(string version, string date)
    {
        using ChangeFolderFixture folder = new();
        folder.AddUnreleased("a", "new");

        Assert.Throws<ArgumentException>(() => ChangeCollector.Collect(folder.Path, version, date));
        Assert.Single(Directory.GetFiles(Path.Combine(folder.Path, "unreleased")));
    }

    [Fact]
    public void An_existing_version_file_or_an_empty_next_version_is_refused()
    {
        using ChangeFolderFixture folder = new();
        Assert.Throws<ArgumentException>(() => ChangeCollector.Collect(folder.Path, "2.2.0", "2026-11-01"));

        folder.AddUnreleased("a", "new");
        folder.AddVersion("2.2.0", "2026-11-01");
        Assert.Throws<ArgumentException>(() => ChangeCollector.Collect(folder.Path, "2.2.0", "2026-11-01"));
        Assert.Single(Directory.GetFiles(Path.Combine(folder.Path, "unreleased")));
    }
}
