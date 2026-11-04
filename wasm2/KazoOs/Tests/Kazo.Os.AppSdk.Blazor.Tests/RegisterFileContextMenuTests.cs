using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.AppSdk.Files;
using Kazo.Os.AppSdk.Icons;

namespace Kazo.Os.AppSdk.Blazor.Tests;

/// <summary>ADR 0151 §2 and ADR 0159: what an app's <c>[RegisterFileContextMenu]</c> method records, and the intent's payload.</summary>
public sealed class RegisterFileContextMenuTests
{
    [Fact]
    public void Register_records_the_manifest_entries_in_declaration_order()
    {
        RegisterFileContextMenuContext context = new();

        context.Register("Menu.Extract", "extract", intent: "com.example.zipper.extract.v1",
            icon: KazoIcons.Vector(IconLibrary.Lucide, "package-open"), types: [" .ZIP ", "application/zip"],
            selection: MenuSelection.Each, after: KazoFileMenuItems.Open);
        context.Register("Menu.Convert", "convert", scope: FileMenuScope.File | FileMenuScope.Directory, afterGroup: KazoFileMenuItems.Copy);
        context.Register("Menu.ToPng", "to-png", intent: "com.example.zipper.to-png.v1", parent: "convert");
        context.Register("Menu.Here", "here", target: "org.hackeros.terminal", intent: "org.hackeros.terminal.open-here.v1",
            scope: FileMenuScope.Background | FileMenuScope.Desktop, selection: MenuSelection.Single, first: true);

        IReadOnlyList<AppContextMenuEntryManifest> entries = context.Entries;

        Assert.Equal(["extract", "convert", "to-png", "here"], entries.Select(static entry => entry.Name));
        AppContextMenuEntryManifest extract = entries[0];
        Assert.Equal("Menu.Extract", extract.Title);
        Assert.Equal(AppContextMenus.SelfTarget, extract.Target);
        Assert.Equal([".zip", "application/zip"], extract.Types);
        Assert.Equal(AppContextMenus.SelectionEach, extract.Selection);
        Assert.Equal(new AppContextMenuIconManifest(AppContextMenus.VectorIcon, "package-open", "lucide"), extract.Icon);
        Assert.Equal([AppContextMenus.FileScope], extract.Scopes);
        // A child without a scope takes its parent's.
        Assert.Equal(["file", "directory"], entries[2].Scopes);
        Assert.Equal(["background", "desktop"], entries[3].Scopes);
        Assert.Equal(AppContextMenus.SelectionSingle, entries[3].Selection);
        Assert.True(entries[3].First);
    }

    [Fact]
    public void Screen_filters_are_set_on_an_entry_registered_before()
    {
        RegisterFileContextMenuContext context = new();
        context.Register("Menu.Wall", "wall", intent: "com.example.wall.v1");
        context.Register("Menu.WallOn", "wall-on");
        context.Register("Menu.Screen2", "wall-2", intent: "com.example.wall.v1", parent: "wall-on");

        context.RestrictToScreens("wall", MenuScreens.Single);
        context.RestrictToScreens("wall-on", MenuScreens.Multiple);
        context.RestrictToScreen("wall-2", 2);

        Assert.Equal([AppContextMenus.ScreensSingle, AppContextMenus.ScreensMultiple, null], context.Entries.Select(static entry => entry.Screens));
        Assert.Equal([null, null, 2], context.Entries.Select(static entry => entry.Screen));
        Assert.Equal(["file"], context.Entries[2].Scopes);

        context.RestrictToScreens("wall", MenuScreens.Any);
        Assert.Null(context.Entries[0].Screens);
        Assert.Throws<ArgumentException>(() => context.RestrictToScreen("missing", 3));
    }

    [Fact]
    public void The_payload_gives_the_entry_name_when_the_OS_sends_it()
    {
        Assert.Equal("wall-2", KazoFileMenuIntent.ReadEntry("""{"paths":["/a.png"],"scope":"File","entry":"wall-2"}"""));
        Assert.Null(KazoFileMenuIntent.ReadEntry("""{"paths":["/a.png"],"scope":"File"}"""));
        Assert.Null(KazoFileMenuIntent.ReadEntry("""{"entry":3}"""));
        Assert.Null(KazoFileMenuIntent.ReadEntry(null));
    }

    [Fact]
    public void The_payload_gives_the_absolute_paths_and_the_scope()
    {
        const string payload = """{"paths":["/home/alice/a.zip","relative.zip",3,"/b"],"scope":"Directory"}""";

        Assert.Equal(["/home/alice/a.zip", "/b"], KazoFileMenuIntent.ReadPaths(payload));
        Assert.Equal(FileMenuScope.Directory, KazoFileMenuIntent.ReadScope(payload));
        Assert.Empty(KazoFileMenuIntent.ReadPaths("{ not json"));
        Assert.Empty(KazoFileMenuIntent.ReadPaths(null));
        Assert.Equal(FileMenuScope.None, KazoFileMenuIntent.ReadScope("""{"scope":"Elsewhere"}"""));
    }

    [Fact]
    public void The_manifest_knows_every_default_entry_of_the_file_menus()
    {
        // The validator (App Abstractions) cannot reference the SDK: both lists must stay the same.
        string[] sdk = [.. typeof(KazoFileMenuItems)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral && field.Name != nameof(KazoFileMenuItems.OpenWithAppPrefix))
            .Select(static field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)];

        Assert.Equal(sdk, AppContextMenus.DefaultItemIds.Order(StringComparer.Ordinal));
    }
}
