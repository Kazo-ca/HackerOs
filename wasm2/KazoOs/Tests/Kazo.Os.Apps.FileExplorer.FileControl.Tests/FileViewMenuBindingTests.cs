using Kazo.Os.App.Abstractions;
using Kazo.Os.Apps.FileExplorer.FileControl.Events;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using static Kazo.Os.Apps.FileExplorer.FileControl.Tests.TestComponentHelpers;

namespace Kazo.Os.Apps.FileExplorer.FileControl.Tests;

/// <summary>
/// ADR 0155 §1: the File Explorer binds this view to the OS context menu through a cascaded
/// <see cref="FileViewMenuBinding"/>. The view then draws no menu of its own, carries the explorer's
/// menu attributes on every item and on the background, and honours the host's choice to forbid
/// renaming or deleting.
/// </summary>
public sealed class FileViewMenuBindingTests
{
    [Fact]
    public async Task Without_a_binding_the_view_keeps_its_own_menu()
    {
        (_, TestableFileView view) = await RenderAsync(binding: null);

        Assert.True(view.UsesOwnMenu);
        Assert.Null(view.MenuAttributesFor(null));
        Assert.True(view.CanRenameItems);
    }

    [Fact]
    public async Task A_binding_puts_the_explorer_menu_on_every_item_and_on_the_background()
    {
        List<FileViewItem?> attached = [];
        FileViewMenuBinding binding = new()
        {
            Attach = item =>
            {
                attached.Add(item);
                return new Dictionary<string, object> { ["data-kazo-menu"] = "files", ["data-kazo-menu-token"] = item?.FileName ?? "background" };
            }
        };
        (_, TestableFileView view) = await RenderAsync(binding);
        FileViewItem a = view.Items.Single(i => i.FileName == "a.txt");

        Assert.False(view.UsesOwnMenu);
        Assert.Equal("a.txt", view.MenuAttributesFor(a)!["data-kazo-menu-token"]);
        Assert.Equal("background", view.MenuAttributesFor(null)!["data-kazo-menu-token"]);
        Assert.Equal([a, null], attached);
    }

    [Fact]
    public async Task A_right_click_outside_the_selection_selects_the_item_only()
    {
        (TestComponentRenderer renderer, TestableFileView view) = await RenderAsync(new FileViewMenuBinding());
        FileViewItem a = view.Items.Single(i => i.FileName == "a.txt");
        FileViewItem b = view.Items.Single(i => i.FileName == "b.txt");
        await OnDispatcherAsync(renderer, () => a.Select());

        await renderer.RunAsync(() => view.OnItemContextMenuAsync(new MouseEventArgs { Button = 2 }, b));

        Assert.Equal([b], view.SelectedItems);
    }

    [Fact]
    public async Task A_right_click_inside_the_selection_keeps_the_whole_selection()
    {
        (TestComponentRenderer renderer, TestableFileView view) = await RenderAsync(new FileViewMenuBinding());
        FileViewItem a = view.Items.Single(i => i.FileName == "a.txt");
        FileViewItem b = view.Items.Single(i => i.FileName == "b.txt");
        await OnDispatcherAsync(renderer, () =>
        {
            a.Select();
            b.Select(additive: true);
        });

        await renderer.RunAsync(() => view.OnItemContextMenuAsync(new MouseEventArgs { Button = 2 }, b));

        Assert.Equal(2, view.SelectedItems.Count);
    }

    [Fact]
    public async Task A_host_without_rename_cannot_enter_rename_mode()
    {
        (TestComponentRenderer renderer, TestableFileView view) = await RenderAsync(new FileViewMenuBinding { AllowRename = false });
        FileViewItem a = view.Items.Single(i => i.FileName == "a.txt");

        await OnDispatcherAsync(renderer, a.Rename);

        Assert.False(a.IsRenaming);
    }

    [Fact]
    public async Task A_host_without_delete_ignores_the_delete_key()
    {
        FileViewDeleteRequestedEventArgs? request = null;
        (TestComponentRenderer renderer, TestableFileView view) = await RenderAsync(
            new FileViewMenuBinding { AllowDelete = false },
            new EventCallback<FileViewDeleteRequestedEventArgs>(null, (Func<FileViewDeleteRequestedEventArgs, Task>)(e =>
            {
                request = e;
                return Task.CompletedTask;
            })));
        await OnDispatcherAsync(renderer, () => view.Items.Single(i => i.FileName == "a.txt").Select());

        await renderer.RunAsync(() => view.DeleteSelectedAsync());

        Assert.Null(request);
        Assert.Equal(2, view.Items.Count);
    }

    private static async Task<(TestComponentRenderer Renderer, TestableFileView View)> RenderAsync(
        FileViewMenuBinding? binding,
        EventCallback<FileViewDeleteRequestedEventArgs> deleteRequested = default)
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            InitialDirectory = VirtualPath.Parse("/home/user"),
            JavaScript = new FakeJSRuntime(),
            MenuBinding = binding,
            DeleteRequested = deleteRequested
        };
        await renderer.RenderAsync(view);
        return (renderer, view);
    }
}
