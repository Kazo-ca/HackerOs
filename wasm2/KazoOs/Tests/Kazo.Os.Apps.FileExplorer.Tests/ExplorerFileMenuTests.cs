using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.AppSdk.Components;
using Kazo.Os.AppSdk.Files;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Kazo.Os.Apps.FileExplorer.Tests;

/// <summary>
/// Plan §7 and ADR 0155 §4: the default file menus the explorer's control draws on the OS menu, by
/// their stable ids, so a host can find, hide or disable them.
/// </summary>
public sealed class ExplorerFileMenuTests
{
    private static readonly HackFileViewItem Notes = new("/home/user/notes.md", "notes.md", false, 12, "text/markdown", DateTimeOffset.UnixEpoch);
    private static readonly HackFileViewItem Photo = new("/home/user/photo.png", "photo.png", false, 99, "image/png", DateTimeOffset.UnixEpoch);
    private static readonly HackFileViewItem Projects = new("/home/user/projects", "projects", true, 0, "inode/directory", DateTimeOffset.UnixEpoch);
    private static readonly HackFileViewItem Music = new("/home/user/music", "music", true, 0, "inode/directory", DateTimeOffset.UnixEpoch);

    [Fact]
    public void One_file_gets_open_open_with_edit_and_properties_groups_in_order()
    {
        HackMenuDefinition menu = Build([Notes], handlers: [App("org.hackeros.text-editor", "Text Editor"), App("org.hackeros.code-editor", "Code Editor")]);

        Assert.Equal(
            [
                [HackFileMenuItems.Open],
                [HackFileMenuItems.OpenWith],
                [HackFileMenuItems.Cut, HackFileMenuItems.Copy, HackFileMenuItems.Rename, HackFileMenuItems.Delete, HackFileMenuItems.DeletePermanently],
                [],
                [HackFileMenuItems.Properties]
            ],
            Ids(menu));
    }

    [Fact]
    public void Open_with_lists_the_handlers_default_first_then_choose_another_app()
    {
        HackMenuDefinition menu = Build([Notes], handlers: [App("org.hackeros.text-editor", "Text Editor"), App("org.hackeros.code-editor", "Code Editor")]);

        HackMenuItemDefinition openWith = menu.Find(HackFileMenuItems.OpenWith)!;
        Assert.Equal(
            [
                ["open-with:org.hackeros.text-editor", "open-with:org.hackeros.code-editor"],
                [HackFileMenuItems.OpenWithOther]
            ],
            openWith.Children.Select(group => group.Items.Select(item => item.Id).ToArray()).ToArray());
        Assert.Equal("Text Editor", openWith.Children[0].Items[0].Title);
        menu.Validate();
    }

    [Fact]
    public void Open_with_without_a_handler_offers_only_choose_another_app()
    {
        HackMenuDefinition menu = Build([Notes]);

        HackMenuItemDefinition openWith = menu.Find(HackFileMenuItems.OpenWith)!;
        Assert.Equal(HackFileMenuItems.OpenWithOther, Assert.Single(Assert.Single(openWith.Children).Items).Id);
    }

    [Fact]
    public void One_folder_gets_paste_into_and_no_open_with()
    {
        HackMenuDefinition menu = Build([Projects], canPaste: true);

        Assert.Null(menu.Find(HackFileMenuItems.OpenWith));
        HackMenuItemDefinition pasteInto = menu.Find(HackFileMenuItems.PasteInto)!;
        Assert.False(pasteInto.IsDisabled);
    }

    [Fact]
    public void Paste_into_is_disabled_when_the_clipboard_is_empty()
    {
        HackMenuDefinition menu = Build([Projects], canPaste: false);

        Assert.True(menu.Find(HackFileMenuItems.PasteInto)!.IsDisabled);
    }

    [Fact]
    public void Several_items_lose_rename_and_open_with_and_disable_properties()
    {
        HackMenuDefinition menu = Build([Notes, Photo]);

        Assert.Null(menu.Find(HackFileMenuItems.Rename));
        Assert.Null(menu.Find(HackFileMenuItems.OpenWith));
        Assert.True(menu.Find(HackFileMenuItems.Properties)!.IsDisabled);
        Assert.False(menu.Find(HackFileMenuItems.Open)!.IsDisabled);
    }

    [Fact]
    public void Open_is_disabled_for_several_folders()
    {
        HackMenuDefinition menu = Build([Projects, Music]);

        Assert.True(menu.Find(HackFileMenuItems.Open)!.IsDisabled);
    }

    [Fact]
    public void The_background_gets_new_folder_paste_refresh_and_properties()
    {
        HackMenuDefinition menu = Build([], canPaste: false);

        Assert.Equal(
            [
                [HackFileMenuItems.NewFolder, HackFileMenuItems.Paste, HackFileMenuItems.Refresh],
                [HackFileMenuItems.Properties]
            ],
            Ids(menu));
        Assert.True(menu.Find(HackFileMenuItems.Paste)!.IsDisabled);
    }

    [Fact]
    public void Features_the_host_forbids_are_left_out()
    {
        HackMenuDefinition item = Build([Notes], features: HackFileViewFeatures.None);
        HackMenuDefinition background = Build([], features: HackFileViewFeatures.None);

        // Open stays; the empty edit group and the apps' group collapse when drawn.
        Assert.Equal([[HackFileMenuItems.Open], [], []], Ids(item));
        Assert.Equal([[HackFileMenuItems.Refresh]], Ids(background));
    }

    [Fact]
    public void Each_entry_runs_its_own_id()
    {
        List<string> clicked = [];
        HackMenuDefinition menu = new("files");
        ExplorerFileMenu.Build(
            menu,
            new ExplorerFileMenuState([Notes], HackFileViewFeatures.All, false, [App("org.hackeros.text-editor", "Text Editor")]),
            id => new EventCallback<object?>(null, (Func<object?, Task>)(_ =>
            {
                clicked.Add(id);
                return Task.CompletedTask;
            })),
            key => key);

        menu.Find(HackFileMenuItems.Delete)!.OnClick.InvokeAsync(null);
        menu.Find(HackFileMenuItems.OpenWith)!.Children[0].Items[0].OnClick.InvokeAsync(null);

        Assert.Equal([HackFileMenuItems.Delete, "open-with:org.hackeros.text-editor"], clicked);
    }

    [Theory]
    [InlineData(0, FileMenuScope.Background)]
    [InlineData(1, FileMenuScope.File)]
    [InlineData(2, FileMenuScope.Directory)]
    [InlineData(3, FileMenuScope.File)]
    public void The_scope_is_a_folder_only_when_every_target_is_one(int set, FileMenuScope expected)
    {
        HackFileViewItem[] targets = set switch
        {
            0 => [],
            1 => [Notes],
            2 => [Projects, Music],
            _ => [Projects, Notes]
        };

        Assert.Equal(expected, ExplorerFileMenu.ScopeOf(targets));
    }

    [Fact]
    public void The_explorer_registers_the_file_view_and_its_dialogs()
    {
        IReadOnlyList<AppComponent> components = new FileExplorerApp().Components;

        Assert.Equal(typeof(ExplorerFileView), Assert.Single(components, c => c.Contract == HackComponentContracts.FileView).Type);
        Assert.Equal(typeof(ExplorerFileDialogs), Assert.Single(components, c => c.Contract == HackComponentContracts.FileDialogs).Type);
    }

    private static HackMenuDefinition Build(
        HackFileViewItem[] targets,
        bool canPaste = false,
        HackFileViewFeatures features = HackFileViewFeatures.All,
        HackAppInfo[]? handlers = null)
    {
        HackMenuDefinition menu = new("files");
        ExplorerFileMenu.Build(
            menu,
            new ExplorerFileMenuState(targets, features, canPaste, handlers ?? []),
            _ => default,
            key => key);
        return menu;
    }

    private static string[][] Ids(HackMenuDefinition menu) =>
        [.. menu.Groups.Select(group => group.Items.Select(item => item.Id).ToArray())];

    private static HackAppInfo App(string id, string name) =>
        new(id, name, string.Empty, AppKind.Window, "utilities", null, "1.0.0", "pub.hackeros", false, false, []);
}
