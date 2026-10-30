namespace HackerOs.Tools.ChangeList.Tests;

/// <summary>ADR 0186 §7: the GitHub Release text, the NuGet release notes and the changelogs.</summary>
public sealed class ReleaseNotesTests
{
    [Fact]
    public void Release_text_lists_every_entry_of_the_version_by_category_in_english()
    {
        using ChangeFolderFixture folder = new();
        folder.AddVersion(
            "2.2.0",
            "2026-11-01",
            ChangeFolderFixture.Entry("fix", "fixed", pr: 12),
            ChangeFolderFixture.Entry("sdk", "developer", audience: "\"developer\""),
            ChangeFolderFixture.Entry("page", "new", pr: 10, links: """["doc/en/wasm/features.md"]"""));
        folder.AddUnreleased("later", "new");

        string text = ReleaseNotes.Release(ChangeFolder.Load(folder.Path), "2.2.0", "https://github.com/o/r");

        Assert.Equal(
            """
            ### New

            - **Title page.** Text page ([#10](https://github.com/o/r/pull/10), [features.md](https://github.com/o/r/blob/main/doc/en/wasm/features.md))

            ### Fixed

            - **Title fix.** Text fix ([#12](https://github.com/o/r/pull/12))

            ### For developers

            - **Title sdk.** Text sdk

            All versions: [CHANGELOG.md](https://github.com/o/r/blob/main/CHANGELOG.md)

            """.ReplaceLineEndings("\n"),
            text);
    }

    [Fact]
    public void Before_the_version_is_collected_the_notes_use_the_unreleased_entries()
    {
        using ChangeFolderFixture folder = new();
        folder.AddVersion("2.1.0", "2026-10-26", ChangeFolderFixture.Entry("old", "new"));
        folder.AddUnreleased("next", "new");

        string text = ReleaseNotes.Release(ChangeFolder.Load(folder.Path), "2.2.0");

        Assert.Contains("Title next", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Title old", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Package_notes_hold_only_developer_entries_and_the_release_link()
    {
        using ChangeFolderFixture folder = new();
        folder.AddUnreleased("user", "new");
        folder.AddUnreleased("sdk", "developer", audience: "\"developer\"", pr: 7);
        folder.AddUnreleased("both", "improved", audience: "\"user\", \"developer\"");

        string text = ReleaseNotes.Package(ChangeFolder.Load(folder.Path), "2.2.0", "https://github.com/o/r");

        Assert.Equal(
            """
            HackerOS 2.2.0, changes for app developers:
            - Title both. Text both
            - Title sdk. Text sdk (#7)
            Release notes: https://github.com/o/r/releases/tag/v2.2.0

            """.ReplaceLineEndings("\n"),
            text);
    }

    [Fact]
    public void Package_notes_say_when_nothing_changed_for_developers()
    {
        using ChangeFolderFixture folder = new();
        folder.AddUnreleased("user", "new");

        string text = ReleaseNotes.Package(ChangeFolder.Load(folder.Path), "2.2.0");

        Assert.StartsWith("HackerOS 2.2.0: no change for app developers.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Changelogs_list_published_versions_newest_first_in_each_language_and_link_each_other()
    {
        using ChangeFolderFixture folder = new();
        folder.AddVersion("2.0.3", "2026-10-26", ChangeFolderFixture.Entry("a", "fixed", links: """["doc/en/x.md"]"""));
        folder.AddVersion("2.1.0", "2026-10-27", ChangeFolderFixture.Entry("b", "new"));
        folder.AddUnreleased("next", "new");
        ChangeFolder changes = ChangeFolder.Load(folder.Path);

        string english = ReleaseNotes.Changelog(changes, "en");
        string french = ReleaseNotes.Changelog(changes, "fr");

        Assert.StartsWith("> [Version française](CHANGELOG-fr.md)", english, StringComparison.Ordinal);
        Assert.StartsWith("> [English version](CHANGELOG.md)", french, StringComparison.Ordinal);
        Assert.True(english.IndexOf("## 2.1.0 (2026-10-27)", StringComparison.Ordinal) < english.IndexOf("## 2.0.3 (2026-10-26)", StringComparison.Ordinal));
        Assert.Contains("- **Titre a.** Texte a ([x.md](doc/fr/x.md))", french, StringComparison.Ordinal);
        Assert.Contains("### Corrigé", french, StringComparison.Ordinal);
        Assert.Contains("([x.md](doc/en/x.md))", english, StringComparison.Ordinal);
        Assert.DoesNotContain("next", english, StringComparison.Ordinal);
    }

    [Fact]
    public void The_changelogs_of_the_repository_are_up_to_date()
    {
        ChangeFolder changes = ChangeFolder.Load(ChangeFolderFixture.RepositoryChanges);
        string root = Path.GetFullPath(Path.Combine(ChangeFolderFixture.RepositoryChanges, "..", "..", ".."));

        Assert.Equal(ReleaseNotes.Changelog(changes, "en"), File.ReadAllText(Path.Combine(root, ReleaseNotes.EnglishChangelog)).ReplaceLineEndings("\n"));
        Assert.Equal(ReleaseNotes.Changelog(changes, "fr"), File.ReadAllText(Path.Combine(root, ReleaseNotes.FrenchChangelog)).ReplaceLineEndings("\n"));
    }
}
