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
    private static readonly KazoFileViewItem Notes = new("/home/user/notes.md", "notes.md", false, 12, "text/markdown", DateTimeOffset.UnixEpoch);
    private static readonly KazoFileViewItem Photo = new("/home/user/photo.png", "photo.png", false, 99, "image/png", DateTimeOffset.UnixEpoch);
    private static readonly KazoFileViewItem Projects = new("/home/user/projects", "projects", true, 0, "inode/directory", DateTimeOffset.UnixEpoch);
    private static readonly KazoFileViewItem Music = new("/home/user/music", "music", true, 0, "inode/directory", DateTimeOffset.UnixEpoch);

    [Fact]
    public void One_file_gets_open_open_with_edit_and_properties_groups_in_order()
    {
        KazoMenuDefinition menu = Build([Notes], handlers: [App("org.hackeros.text-editor", "Text Editor"), App("org.hackeros.code-editor", "Code Editor")]);

        Assert.Equal(
            [
                [KazoFileMenuItems.Open],
                [KazoFileMenuItems.OpenWith],
                [KazoFileMenuItems.Cut, KazoFileMenuItems.Copy, KazoFileMenuItems.Rename, KazoFileMenuItems.Delete, KazoFileMenuItems.DeletePermanently],
                [],
                [KazoFileMenuItems.Properties]
            ],
            Ids(menu));
    }

    [Fact]
    public void Open_with_lists_the_handlers_default_first_then_choose_another_app()
    {
        KazoMenuDefinition menu = Build([Notes], handlers: [App("org.hackeros.text-editor", "Text Editor"), App("org.hackeros.code-editor", "Code Editor")]);

        KazoMenuItemDefinition openWith = menu.Find(KazoFileMenuItems.OpenWith)!;
        Assert.Equal(
            [
                ["open-with:org.hackeros.text-editor", "open-with:org.hackeros.code-editor"],
                [KazoFileMenuItems.OpenWithOther]
            ],
            openWith.Children.Select(group => group.Items.Select(item => item.Id).ToArray()).ToArray());
        Assert.Equal("Text Editor", openWith.Children[0].Items[0].Title);
        menu.Validate();
    }

    [Fact]
    public void Open_with_without_a_handler_offers_only_choose_another_app()
    {
        KazoMenuDefinition menu = Build([Notes]);

        KazoMenuItemDefinition openWith = menu.Find(KazoFileMenuItems.OpenWith)!;
        Assert.Equal(KazoFileMenuItems.OpenWithOther, Assert.Single(Assert.Single(openWith.Children).Items).Id);
    }

    [Fact]
    public void One_folder_gets_paste_into_and_no_open_with()
    {
        KazoMenuDefinition menu = Build([Projects], canPaste: true);

        Assert.Null(menu.Find(KazoFileMenuItems.OpenWith));
        KazoMenuItemDefinition pasteInto = menu.Find(KazoFileMenuItems.PasteInto)!;
        Assert.False(pasteInto.IsDisabled);
    }

    [Fact]
    public void Paste_into_is_disabled_when_the_clipboard_is_empty()
    {
        KazoMenuDefinition menu = Build([Projects], canPaste: false);

        Assert.True(menu.Find(KazoFileMenuItems.PasteInto)!.IsDisabled);
    }

    [Fact]
    public void Several_items_lose_rename_and_open_with_and_disable_properties()
    {
        KazoMenuDefinition menu = Build([Notes, Photo]);

        Assert.Null(menu.Find(KazoFileMenuItems.Rename));
        Assert.Null(menu.Find(KazoFileMenuItems.OpenWith));
        Assert.True(menu.Find(KazoFileMenuItems.Properties)!.IsDisabled);
        Assert.False(menu.Find(KazoFileMenuItems.Open)!.IsDisabled);
    }

    [Fact]
    public void Open_is_disabled_for_several_folders()
    {
        KazoMenuDefinition menu = Build([Projects, Music]);

        Assert.True(menu.Find(KazoFileMenuItems.Open)!.IsDisabled);
    }

    [Fact]
    public void The_background_gets_new_folder_paste_refresh_and_properties()
    {
        KazoMenuDefinition menu = Build([], canPaste: false);

        Assert.Equal(
            [
                [KazoFileMenuItems.NewFolder, KazoFileMenuItems.Paste, KazoFileMenuItems.Refresh],
                [KazoFileMenuItems.Properties]
            ],
            Ids(menu));
        Assert.True(menu.Find(KazoFileMenuItems.Paste)!.IsDisabled);
    }

    [Fact]
    public void Features_the_host_forbids_are_left_out()
    {
        KazoMenuDefinition item = Build([Notes], features: KazoFileViewFeatures.None);
        KazoMenuDefinition background = Build([], features: KazoFileViewFeatures.None);

        // Open stays; the empty edit group and the apps' group collapse when drawn.
        Assert.Equal([[KazoFileMenuItems.Open], [], []], Ids(item));
        Assert.Equal([[KazoFileMenuItems.Refresh]], Ids(background));
    }

    [Fact]
    public void Each_entry_runs_its_own_id()
    {
        List<string> clicked = [];
        KazoMenuDefinition menu = new("files");
        ExplorerFileMenu.Build(
            menu,
            new ExplorerFileMenuState([Notes], KazoFileViewFeatures.All, false, [App("org.hackeros.text-editor", "Text Editor")]),
            id => new EventCallback<object?>(null, (Func<object?, Task>)(_ =>
            {
                clicked.Add(id);
                return Task.CompletedTask;
            })),
            key => key);

        menu.Find(KazoFileMenuItems.Delete)!.OnClick.InvokeAsync(null);
        menu.Find(KazoFileMenuItems.OpenWith)!.Children[0].Items[0].OnClick.InvokeAsync(null);

        Assert.Equal([KazoFileMenuItems.Delete, "open-with:org.hackeros.text-editor"], clicked);
    }

    [Theory]
    [InlineData(0, FileMenuScope.Background)]
    [InlineData(1, FileMenuScope.File)]
    [InlineData(2, FileMenuScope.Directory)]
    [InlineData(3, FileMenuScope.File)]
    public void The_scope_is_a_folder_only_when_every_target_is_one(int set, FileMenuScope expected)
    {
        KazoFileViewItem[] targets = set switch
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

        Assert.Equal(typeof(ExplorerFileView), Assert.Single(components, c => c.Contract == KazoComponentContracts.FileView).Type);
        Assert.Equal(typeof(ExplorerFileDialogs), Assert.Single(components, c => c.Contract == KazoComponentContracts.FileDialogs).Type);
    }

    private static KazoMenuDefinition Build(
        KazoFileViewItem[] targets,
        bool canPaste = false,
        KazoFileViewFeatures features = KazoFileViewFeatures.All,
        KazoAppInfo[]? handlers = null)
    {
        KazoMenuDefinition menu = new("files");
        ExplorerFileMenu.Build(
            menu,
            new ExplorerFileMenuState(targets, features, canPaste, handlers ?? []),
            _ => default,
            key => key);
        return menu;
    }

    private static string[][] Ids(KazoMenuDefinition menu) =>
        [.. menu.Groups.Select(group => group.Items.Select(item => item.Id).ToArray())];

    private static KazoAppInfo App(string id, string name) =>
        new(id, name, string.Empty, AppKind.Window, "utilities", null, "1.0.0", "pub.hackeros", false, false, []);
}
