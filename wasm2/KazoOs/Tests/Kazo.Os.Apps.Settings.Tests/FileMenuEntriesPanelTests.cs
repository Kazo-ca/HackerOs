using Kazo.Os.AppSdk.Administration;
using Xunit;

namespace Kazo.Os.Apps.Settings.Tests;

/// <summary>Settings &gt; File menus lists each submenu right under its parent (ADR 0159 §8).</summary>
public sealed class FileMenuEntriesPanelTests
{
    [Fact]
    public void Entries_are_listed_depth_first_in_declaration_order()
    {
        HackFileMenuEntryInfo[] entries =
        [
            Entry("convert"),
            Entry("hash"),
            Entry("to-png", parent: "convert"),
            Entry("to-jpeg", parent: "convert"),
            Entry("quality", parent: "to-jpeg"),
            Entry("lost", parent: "missing")
        ];

        Assert.Equal(
            ["convert", "to-png", "to-jpeg", "quality", "hash", "lost"],
            FileMenuEntriesPanel.Ordered(entries).Select(static entry => entry.Name));
    }

    private static HackFileMenuEntryInfo Entry(string name, string? parent = null) =>
        new("com.example.tools", "Tools", name, name, parent, ["file"], IsHidden: false);
}
