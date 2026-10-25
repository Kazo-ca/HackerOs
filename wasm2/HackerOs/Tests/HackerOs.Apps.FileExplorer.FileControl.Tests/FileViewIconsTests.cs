using HackerOs.App.Abstractions;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using static HackerOs.Apps.FileExplorer.FileControl.Tests.TestComponentHelpers;

namespace HackerOs.Apps.FileExplorer.FileControl.Tests;

/// <summary>
/// Tests for <c>FV-003</c> — <see cref="FileViewIcons"/>'s tile click/activate/keydown handlers (which
/// mirror <c>FileViewDetails</c> exactly) and its marquee multi-select math/JS-interop wiring, driven
/// through a <see cref="FakeJSRuntime"/> since there is no real browser in this xunit process.
/// </summary>
public sealed class FileViewIconsTests
{
    [Fact]
    public async Task OnTileClick_selects_the_item()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture);
        TestableFileViewIcons icons = await RenderIconsAsync(renderer, view);
        FileViewItem item = view.Items.Single(i => i.FileName == "a.txt");

        await OnDispatcherAsync(renderer, () => icons.OnTileClick(item, new MouseEventArgs()));

        Assert.True(item.IsSelected);
    }

    [Fact]
    public async Task OnTileDoubleClickAsync_activates_the_item()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture);
        TestableFileViewIcons icons = await RenderIconsAsync(renderer, view);
        FileViewItem directory = view.Items.Single(i => i.FileName == "Documents");

        await renderer.RunAsync(() => icons.OnTileDoubleClickAsync(directory));

        Assert.Equal(VirtualPath.Parse("/home/user/Documents"), view.CurrentDirectory);
    }

    [Fact]
    public async Task With_TapOpens_a_tap_on_a_folder_opens_it()
    {
        // ADR 0171 §2: the File Explorer's mobile window opens an item with one tap.
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture);
        TestableFileViewIcons icons = await RenderIconsAsync(renderer, view);
        FileViewItem directory = view.Items.Single(i => i.FileName == "Documents");
        view.TapOpens = true;

        await renderer.RunAsync(() => icons.OnTileClick(directory, new MouseEventArgs()));

        Assert.Equal(VirtualPath.Parse("/home/user/Documents"), view.CurrentDirectory);
    }

    [Fact]
    public async Task With_TapOpens_the_double_click_does_not_open_a_second_time()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture);
        TestableFileViewIcons icons = await RenderIconsAsync(renderer, view);
        FileViewItem directory = view.Items.Single(i => i.FileName == "Documents");
        view.TapOpens = true;

        await renderer.RunAsync(() => icons.OnTileDoubleClickAsync(directory));

        Assert.Equal(VirtualPath.Parse("/home/user"), view.CurrentDirectory);
    }

    [Fact]
    public async Task In_the_selection_mode_a_tap_adds_then_removes_the_item()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture);
        TestableFileViewIcons icons = await RenderIconsAsync(renderer, view);
        FileViewItem a = view.Items.Single(i => i.FileName == "a.txt");
        FileViewItem b = view.Items.Single(i => i.FileName == "b.txt");
        view.TapOpens = true;
        view.TapTogglesSelection = true;

        await renderer.RunAsync(() => icons.OnTileClick(a, new MouseEventArgs()));
        await renderer.RunAsync(() => icons.OnTileClick(b, new MouseEventArgs()));
        Assert.True(a.IsSelected);
        Assert.True(b.IsSelected);

        await renderer.RunAsync(() => icons.OnTileClick(a, new MouseEventArgs()));
        Assert.False(a.IsSelected);
        Assert.True(b.IsSelected);
        Assert.Equal(VirtualPath.Parse("/home/user"), view.CurrentDirectory);
    }

    [Fact]
    public async Task OnTileKeyDownAsync_F2_enters_rename_mode()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture);
        TestableFileViewIcons icons = await RenderIconsAsync(renderer, view);
        FileViewItem item = view.Items.Single(i => i.FileName == "a.txt");

        await OnDispatcherAsync(renderer, () => icons.OnTileKeyDownAsync(item, new KeyboardEventArgs { Key = "F2" }));

        Assert.True(item.IsRenaming);
        Assert.True(item.IsSelected);
    }

    [Fact]
    public async Task Marquee_select_replaces_the_selection_with_intersecting_tiles()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        await fixture.CreateFileAsync("/home/user/c.txt");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture);
        FakeJSRuntime jsRuntime = NewJsRuntime(
            containerLeft: 0, containerTop: 0,
            intersecting: ["/home/user/a.txt", "/home/user/b.txt"]);
        TestableFileViewIcons icons = await RenderIconsAsync(renderer, view, jsRuntime);

        await renderer.RunAsync(() => icons.OnGridMouseDownAsync(new MouseEventArgs { Button = 0, ClientX = 0, ClientY = 0 }));
        await OnDispatcherAsync(renderer, () => icons.OnGridMouseMove(new MouseEventArgs { ClientX = 50, ClientY = 50 }));
        await renderer.RunAsync(() => icons.OnGridMouseUpAsync(new MouseEventArgs { ClientX = 50, ClientY = 50 }));

        Assert.Equal(2, view.SelectedItems.Count);
        Assert.Contains(view.SelectedItems, i => i.FileName == "a.txt");
        Assert.Contains(view.SelectedItems, i => i.FileName == "b.txt");
        Assert.DoesNotContain(view.SelectedItems, i => i.FileName == "c.txt");
    }

    [Fact]
    public async Task Marquee_select_is_additive_when_Ctrl_is_held()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture);
        FakeJSRuntime jsRuntime = NewJsRuntime(containerLeft: 0, containerTop: 0, intersecting: ["/home/user/b.txt"]);
        TestableFileViewIcons icons = await RenderIconsAsync(renderer, view, jsRuntime);
        await OnDispatcherAsync(renderer, () => view.SelectByName("a.txt"));

        await renderer.RunAsync(() => icons.OnGridMouseDownAsync(
            new MouseEventArgs { Button = 0, ClientX = 0, ClientY = 0, CtrlKey = true }));
        await OnDispatcherAsync(renderer, () => icons.OnGridMouseMove(new MouseEventArgs { ClientX = 50, ClientY = 50 }));
        await renderer.RunAsync(() => icons.OnGridMouseUpAsync(new MouseEventArgs { ClientX = 50, ClientY = 50 }));

        Assert.Equal(2, view.SelectedItems.Count);
        Assert.Contains(view.SelectedItems, i => i.FileName == "a.txt");
        Assert.Contains(view.SelectedItems, i => i.FileName == "b.txt");
    }

    [Fact]
    public async Task Marquee_below_the_drag_threshold_leaves_the_selection_unchanged()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture);
        FakeJSObjectReference module = NewModule(intersecting: ["/home/user/a.txt"]);
        FakeJSRuntime jsRuntime = new();
        jsRuntime.Handlers["import"] = _ => module;
        TestableFileViewIcons icons = await RenderIconsAsync(renderer, view, jsRuntime);

        await renderer.RunAsync(() => icons.OnGridMouseDownAsync(new MouseEventArgs { Button = 0, ClientX = 10, ClientY = 10 }));
        await renderer.RunAsync(() => icons.OnGridMouseUpAsync(new MouseEventArgs { ClientX = 11, ClientY = 11 }));

        Assert.Empty(view.SelectedItems);
        Assert.DoesNotContain("getIntersectingPaths", module.CalledIdentifiers);
    }

    [Fact]
    public async Task OnGridMouseDownAsync_does_nothing_when_multiselect_is_disallowed()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, allowMultiSelect: false);
        FakeJSRuntime jsRuntime = NewJsRuntime(containerLeft: 0, containerTop: 0, intersecting: []);
        TestableFileViewIcons icons = await RenderIconsAsync(renderer, view, jsRuntime);

        await renderer.RunAsync(() => icons.OnGridMouseDownAsync(new MouseEventArgs { Button = 0, ClientX = 0, ClientY = 0 }));
        await renderer.RunAsync(() => icons.OnGridMouseUpAsync(new MouseEventArgs { ClientX = 50, ClientY = 50 }));

        Assert.Empty(jsRuntime.CalledIdentifiers);
    }

    [Fact]
    public async Task IsTabStop_is_true_only_for_the_first_tile_when_nothing_is_selected()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture);
        TestableFileViewIcons icons = await RenderIconsAsync(renderer, view);
        FileViewItem a = view.Items.Single(i => i.FileName == "a.txt");
        FileViewItem b = view.Items.Single(i => i.FileName == "b.txt");

        Assert.True(icons.IsTabStop(a));
        Assert.False(icons.IsTabStop(b));
    }

    [Fact]
    public async Task OnTileKeyDownAsync_ArrowRight_selects_the_next_tile_and_focuses_it()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        (FakeJSRuntime ownerJsRuntime, FakeJSObjectReference ownerModule) = NewFocusJsRuntime();
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, ownerJsRuntime: ownerJsRuntime);
        TestableFileViewIcons icons = await RenderIconsAsync(renderer, view);
        FileViewItem a = view.Items.Single(i => i.FileName == "a.txt");
        FileViewItem b = view.Items.Single(i => i.FileName == "b.txt");

        await renderer.RunAsync(() => icons.OnTileKeyDownAsync(a, new KeyboardEventArgs { Key = "ArrowRight" }));

        Assert.True(b.IsSelected);
        (string Identifier, object?[]? Args) call = ownerModule.Calls.Single(c => c.Identifier == "focusItem");
        Assert.Equal("/home/user/b.txt", call.Args![1]);
    }

    [Fact]
    public async Task OnTileKeyDownAsync_ArrowLeft_at_the_first_tile_does_nothing()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        (FakeJSRuntime ownerJsRuntime, FakeJSObjectReference ownerModule) = NewFocusJsRuntime();
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, ownerJsRuntime: ownerJsRuntime);
        TestableFileViewIcons icons = await RenderIconsAsync(renderer, view);
        FileViewItem a = view.Items.Single(i => i.FileName == "a.txt");

        await renderer.RunAsync(() => icons.OnTileKeyDownAsync(a, new KeyboardEventArgs { Key = "ArrowLeft" }));

        Assert.Empty(view.SelectedItems);
        Assert.Empty(ownerModule.Calls);
    }

    private static FakeJSRuntime NewJsRuntime(double containerLeft, double containerTop, string[] intersecting)
    {
        FakeJSObjectReference module = NewModule(intersecting, containerLeft, containerTop);
        FakeJSRuntime runtime = new();
        runtime.Handlers["import"] = _ => module;
        return runtime;
    }

    private static FakeJSObjectReference NewModule(string[] intersecting, double containerLeft = 0, double containerTop = 0)
    {
        FakeJSObjectReference module = new();
        module.Handlers["getContainerRect"] = _ => new ContainerRect { Left = containerLeft, Top = containerTop };
        module.Handlers["getIntersectingPaths"] = _ => intersecting;
        return module;
    }

    private static async Task<(TestComponentRenderer Renderer, TestableFileView View)> RenderViewAsync(
        FileViewTestFixture fixture, bool allowMultiSelect = true, IJSRuntime? ownerJsRuntime = null)
    {
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            InitialDirectory = VirtualPath.Parse("/home/user"),
            AllowMultiSelect = allowMultiSelect,
            JavaScript = ownerJsRuntime ?? new FakeJSRuntime()
        };
        await renderer.RenderAsync(view);
        return (renderer, view);
    }

    private static Task<TestableFileViewIcons> RenderIconsAsync(
        TestComponentRenderer renderer, FileView view, FakeJSRuntime? jsRuntime = null)
    {
        TestableFileViewIcons icons = new() { Owner = view, JavaScript = jsRuntime ?? new FakeJSRuntime() };
        return renderer.RenderAsync(icons);
    }

    /// <summary>Suppresses the real markup (ShellIcon/HackerIcon) so tests can attach a real RenderHandle and exercise the C# backing directly.</summary>
    private sealed class TestableFileViewIcons : FileViewIcons
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            // Intentionally empty: these tests exercise the backing logic directly, not rendered markup.
        }
    }
}
