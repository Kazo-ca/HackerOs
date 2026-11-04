using Kazo.Os.App.Abstractions;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using static Kazo.Os.Apps.FileExplorer.FileControl.Tests.TestComponentHelpers;

namespace Kazo.Os.Apps.FileExplorer.FileControl.Tests;

/// <summary>
/// Tests for <see cref="FileViewDetails"/>'s roving-tabindex/arrow-key navigation (<c>FV-011</c>): the
/// click/dblclick/rename/sort handlers are otherwise identical to <c>FileViewIcons</c>/<c>FileViewTree</c>
/// (already covered there and by <c>FileViewTests.cs</c>'s exercise of <see cref="FileView"/>'s shared
/// backing), so this file focuses on what's new here rather than re-testing shared plumbing.
/// </summary>
public sealed class FileViewDetailsTests
{
    [Fact]
    public async Task IsTabStop_is_true_only_for_the_first_row_when_nothing_is_selected()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture);
        TestableFileViewDetails details = await RenderDetailsAsync(renderer, view);
        FileViewItem a = view.Items.Single(i => i.FileName == "a.txt");
        FileViewItem b = view.Items.Single(i => i.FileName == "b.txt");

        Assert.True(details.IsTabStop(a));
        Assert.False(details.IsTabStop(b));
    }

    [Fact]
    public async Task IsTabStop_follows_the_selection()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture);
        TestableFileViewDetails details = await RenderDetailsAsync(renderer, view);
        FileViewItem a = view.Items.Single(i => i.FileName == "a.txt");
        FileViewItem b = view.Items.Single(i => i.FileName == "b.txt");
        await OnDispatcherAsync(renderer, () => view.SelectByName("b.txt"));

        Assert.False(details.IsTabStop(a));
        Assert.True(details.IsTabStop(b));
    }

    [Fact]
    public async Task OnRowKeyDownAsync_ArrowDown_selects_the_next_row_and_focuses_it()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        (FakeJSRuntime jsRuntime, FakeJSObjectReference module) = NewFocusJsRuntime();
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, jsRuntime);
        TestableFileViewDetails details = await RenderDetailsAsync(renderer, view);
        FileViewItem a = view.Items.Single(i => i.FileName == "a.txt");
        FileViewItem b = view.Items.Single(i => i.FileName == "b.txt");

        await renderer.RunAsync(() => details.OnRowKeyDownAsync(a, new KeyboardEventArgs { Key = "ArrowDown" }));

        Assert.True(b.IsSelected);
        (string Identifier, object?[]? Args) call = module.Calls.Single(c => c.Identifier == "focusItem");
        Assert.Equal("/home/user/b.txt", call.Args![1]);
    }

    [Fact]
    public async Task OnRowKeyDownAsync_ArrowUp_at_the_first_row_does_nothing()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        (FakeJSRuntime jsRuntime, FakeJSObjectReference module) = NewFocusJsRuntime();
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, jsRuntime);
        TestableFileViewDetails details = await RenderDetailsAsync(renderer, view);
        FileViewItem a = view.Items.Single(i => i.FileName == "a.txt");

        await renderer.RunAsync(() => details.OnRowKeyDownAsync(a, new KeyboardEventArgs { Key = "ArrowUp" }));

        Assert.Empty(view.SelectedItems);
        Assert.Empty(module.Calls);
    }

    [Fact]
    public async Task OnRowKeyDownAsync_ArrowDown_at_the_last_row_does_nothing()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        (FakeJSRuntime jsRuntime, FakeJSObjectReference module) = NewFocusJsRuntime();
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, jsRuntime);
        TestableFileViewDetails details = await RenderDetailsAsync(renderer, view);
        FileViewItem b = view.Items.Single(i => i.FileName == "b.txt");

        await renderer.RunAsync(() => details.OnRowKeyDownAsync(b, new KeyboardEventArgs { Key = "ArrowDown" }));

        Assert.Empty(view.SelectedItems);
        Assert.Empty(module.Calls);
    }

    [Fact]
    public async Task SortedItems_defaults_to_folders_before_files_each_group_alphabetical()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/banana.txt");
        await fixture.CreateDirectoryAsync("/home/user/Zebra");
        await fixture.CreateFileAsync("/home/user/apple.txt");
        await fixture.CreateDirectoryAsync("/home/user/Apricot");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture);
        TestableFileViewDetails details = await RenderDetailsAsync(renderer, view);

        Assert.Equal(
            ["Apricot", "Zebra", "apple.txt", "banana.txt"],
            details.SortedItems.Select(item => item.FileName));
    }

    [Fact]
    public async Task SortedItems_keeps_folders_grouped_first_when_sorted_by_another_column_descending()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/banana.txt");
        await fixture.CreateDirectoryAsync("/home/user/Zebra");
        await fixture.CreateFileAsync("/home/user/apple.txt");
        await fixture.CreateDirectoryAsync("/home/user/Apricot");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture);
        TestableFileViewDetails details = await RenderDetailsAsync(renderer, view);

        details.ToggleSort("name");

        Assert.Equal(
            ["Zebra", "Apricot", "banana.txt", "apple.txt"],
            details.SortedItems.Select(item => item.FileName));
    }

    private static async Task<(TestComponentRenderer Renderer, TestableFileView View)> RenderViewAsync(
        FileViewTestFixture fixture, IJSRuntime? jsRuntime = null)
    {
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            InitialDirectory = VirtualPath.Parse("/home/user"),
            JavaScript = jsRuntime ?? new FakeJSRuntime()
        };
        await renderer.RenderAsync(view);
        return (renderer, view);
    }

    private static Task<TestableFileViewDetails> RenderDetailsAsync(TestComponentRenderer renderer, FileView owner) =>
        renderer.RenderAsync(new TestableFileViewDetails { Owner = owner });

    /// <summary>Suppresses the real markup (ShellIcon/KazoIcon) so tests can attach a real RenderHandle and exercise the C# backing directly.</summary>
    private sealed class TestableFileViewDetails : FileViewDetails
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            // Intentionally empty: these tests exercise the backing logic directly, not rendered markup.
        }
    }
}
