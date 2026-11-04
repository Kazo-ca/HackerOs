using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Tests.Settings;

/// <summary>Contract of the layering engine (ADR 0044 sections 3 to 6).</summary>
public sealed class LayeredConfigResolverTests
{
    private const string SystemPath = "/etc/org.example.editor/settings.config";
    private const string UserPath = "/home/alice/.config/org.example.editor/settings.config";

    private static readonly SettingsSchema Schema = new(
        1,
        [
            new SettingFieldDeclaration("theme", SettingValueType.Enum, "dark", SettingSensitivity.Public, ["dark", "light"]),
            new SettingFieldDeclaration("autosave", SettingValueType.Boolean, "true", SettingSensitivity.Public),
            new SettingFieldDeclaration("tab-size", SettingValueType.Integer, "4", SettingSensitivity.Public, Group: "Editor")
        ]);

    private static LayeredConfigSnapshot Resolve(string? system, string? user) =>
        LayeredConfigResolver.Resolve(Schema, system, user, SystemPath, UserPath);

    [Fact]
    public void No_files_gives_the_declared_defaults()
    {
        LayeredConfigSnapshot snapshot = Resolve(null, null);

        Assert.Equal("dark", snapshot.Values["theme"]);
        Assert.Equal("4", snapshot.Values["Editor.tab-size"]);
        Assert.All(snapshot.Sources.Values, source => Assert.Equal(SettingLayer.Default, source));
        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public void User_beats_system_which_beats_default()
    {
        LayeredConfigSnapshot snapshot = Resolve(
            "theme=light\nautosave=false\n",
            "theme=dark\n");

        Assert.Equal(("dark", SettingLayer.User), (snapshot.Values["theme"], snapshot.Sources["theme"]));
        Assert.Equal(("false", SettingLayer.System), (snapshot.Values["autosave"], snapshot.Sources["autosave"]));
        Assert.Equal(("4", SettingLayer.Default), (snapshot.Values["Editor.tab-size"], snapshot.Sources["Editor.tab-size"]));
    }

    [Fact]
    public void Locked_key_takes_the_system_value_and_ignores_the_user_layer()
    {
        LayeredConfigSnapshot snapshot = Resolve(
            "theme=light\n[locked]\ntheme=true\n",
            "theme=dark\n");

        Assert.Equal(("light", SettingLayer.System), (snapshot.Values["theme"], snapshot.Sources["theme"]));
        Assert.Contains("theme", snapshot.LockedKeys);
    }

    [Fact]
    public void Locked_key_without_a_system_value_is_pinned_to_the_default()
    {
        LayeredConfigSnapshot snapshot = Resolve("[locked]\nEditor.tab-size=true\n", "[Editor]\ntab-size=8\n");

        Assert.Equal(("4", SettingLayer.Default), (snapshot.Values["Editor.tab-size"], snapshot.Sources["Editor.tab-size"]));
    }

    [Fact]
    public void Locked_section_in_the_user_layer_is_ignored_with_a_warning()
    {
        LayeredConfigSnapshot snapshot = Resolve(null, "theme=light\n[locked]\ntheme=true\n");

        Assert.Empty(snapshot.LockedKeys);
        Assert.Equal("light", snapshot.Values["theme"]);
        LayeredConfigWarning warning = Assert.Single(snapshot.Warnings);
        Assert.Equal($"settings: {UserPath}:2: [locked] is only honored in the system layer, section ignored", warning.ToLogLine());
    }

    [Fact]
    public void Invalid_value_falls_back_to_the_next_layer_and_says_which()
    {
        LayeredConfigSnapshot snapshot = Resolve("theme=light\n", "theme=purple\nautosave=perhaps\n");

        Assert.Equal(("light", SettingLayer.System), (snapshot.Values["theme"], snapshot.Sources["theme"]));
        Assert.Equal(("true", SettingLayer.Default), (snapshot.Values["autosave"], snapshot.Sources["autosave"]));
        Assert.Equal(
            [
                $"settings: {UserPath}:1: invalid value for 'theme' (expected one of: dark, light), using system value",
                $"settings: {UserPath}:2: invalid value for 'autosave' (expected boolean), using default"
            ],
            snapshot.Warnings.Select(warning => warning.ToLogLine()));
    }

    [Fact]
    public void Malformed_lines_and_unknown_keys_are_skipped_line_by_line()
    {
        LayeredConfigSnapshot snapshot = Resolve(
            null,
            "# comment\n\nthis is not a setting\ncolour=red\ntheme=light\n[Editor\ntab-size=2\n");

        Assert.Equal("light", snapshot.Values["theme"]);
        Assert.Equal("4", snapshot.Values["Editor.tab-size"]);
        Assert.Equal(
            [
                $"settings: {UserPath}:3: malformed line ignored",
                $"settings: {UserPath}:4: unknown key 'colour' ignored",
                $"settings: {UserPath}:6: malformed line ignored"
            ],
            snapshot.Warnings.Select(warning => warning.ToLogLine()));
    }

    [Fact]
    public void Duplicate_key_last_valid_one_wins_and_an_invalid_duplicate_does_not_erase_it()
    {
        LayeredConfigSnapshot snapshot = Resolve(null, "theme=light\ntheme=dark\nautosave=false\nautosave=nope\n");

        Assert.Equal("dark", snapshot.Values["theme"]);
        Assert.Equal(("false", SettingLayer.User), (snapshot.Values["autosave"], snapshot.Sources["autosave"]));
        Assert.Single(snapshot.Warnings);
    }

    [Fact]
    public void Lock_entries_naming_unknown_keys_or_bad_values_are_reported()
    {
        LayeredConfigSnapshot snapshot = Resolve("[locked]\nfont=true\ntheme=maybe\n", null);

        Assert.Empty(snapshot.LockedKeys);
        Assert.Equal(2, snapshot.Warnings.Count);
    }

    [Fact]
    public void Schema_version_line_is_accepted_silently()
    {
        Assert.Empty(Resolve("schemaVersion=1\n", "schemaVersion=1\n").Warnings);
    }

    [Fact]
    public void Locked_is_a_reserved_group_name()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SettingsSchema(
            1,
            [new SettingFieldDeclaration("theme", SettingValueType.String, "x", SettingSensitivity.Public, Group: "locked")]));
    }
}
