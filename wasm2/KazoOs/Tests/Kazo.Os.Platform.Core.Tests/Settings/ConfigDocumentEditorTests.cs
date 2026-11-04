using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Tests.Settings;

/// <summary>Writes touch one line and keep everything a human wrote (ADR 0044 section 6).</summary>
public sealed class ConfigDocumentEditorTests
{
    [Fact]
    public void SetValue_on_a_missing_file_creates_the_line()
    {
        Assert.Equal("theme=light\n", ConfigDocumentEditor.SetValue(null, null, "theme", "light"));
    }

    [Fact]
    public void SetValue_replaces_in_place_and_keeps_comments_blank_lines_and_unknown_keys()
    {
        const string before = "# my editor\n\ntheme=dark # not a comment, part of the value\ncolour=red\n";

        string after = ConfigDocumentEditor.SetValue(before, null, "theme", "light");

        Assert.Equal("# my editor\n\ntheme=light\ncolour=red\n", after);
    }

    [Fact]
    public void SetValue_replaces_the_last_duplicate_because_it_is_the_one_that_wins()
    {
        Assert.Equal("theme=dark\ntheme=light\n", ConfigDocumentEditor.SetValue("theme=dark\ntheme=dark\n", null, "theme", "light"));
    }

    [Fact]
    public void SetValue_inserts_root_keys_before_the_first_section()
    {
        string after = ConfigDocumentEditor.SetValue("theme=dark\n[Editor]\ntab-size=2\n", null, "autosave", "false");

        Assert.Equal("theme=dark\nautosave=false\n[Editor]\ntab-size=2\n", after);
    }

    [Fact]
    public void SetValue_appends_to_an_existing_section_or_creates_it()
    {
        Assert.Equal(
            "[Editor]\ntab-size=2\nwrap=true\n[Other]\nx=1\n",
            ConfigDocumentEditor.SetValue("[Editor]\ntab-size=2\n[Other]\nx=1\n", "Editor", "wrap", "true"));
        Assert.Equal(
            "theme=dark\n\n[Editor]\ntab-size=8\n",
            ConfigDocumentEditor.SetValue("theme=dark\n", "Editor", "tab-size", "8"));
    }

    [Fact]
    public void Values_round_trip_through_escaping()
    {
        string after = ConfigDocumentEditor.SetValue(null, null, "greeting", "a=b # c\nd");
        LayeredConfigSnapshot snapshot = LayeredConfigResolver.Resolve(
            new SettingsSchema(1, [new SettingFieldDeclaration("greeting", SettingValueType.String, "", SettingSensitivity.Public)]),
            null,
            after,
            "/etc/x",
            "/home/x");

        Assert.Equal("a=b # c\nd", snapshot.Values["greeting"]);
    }

    [Fact]
    public void RemoveValue_drops_every_occurrence_and_nothing_else()
    {
        Assert.Equal(
            "# keep\ncolour=red\n",
            ConfigDocumentEditor.RemoveValue("# keep\ntheme=dark\ncolour=red\ntheme=light\n", null, "theme"));
    }
}
