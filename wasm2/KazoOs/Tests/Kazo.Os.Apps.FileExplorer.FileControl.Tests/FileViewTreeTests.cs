using Kazo.Os.App.Abstractions;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using static Kazo.Os.Apps.FileExplorer.FileControl.Tests.TestComponentHelpers;

namespace Kazo.Os.Apps.FileExplorer.FileControl.Tests;

/// <summary>
/// Tests for <c>FV-004</c> — <see cref="FileViewTree"/>'s root node building/caching and
/// <see cref="FileViewTreeNode"/>'s lazy-expand and select/activate/keydown handlers (which mirror
/// <c>FileViewDetails</c>/<c>FileViewIcons</c> exactly except for expansion).
/// </summary>
public sealed class FileViewTreeTests
{
    [Fact]
    public async Task Hidden_descendants_follow_filter_changes_even_after_the_tree_is_cached()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        await fixture.CreateDirectoryAsync("/home/user/Documents/.mobile");
        await fixture.CreateFileAsync("/home/user/Documents/report.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = await RenderViewAsync(fixture, renderer);
        bool showHidden = false;
        view.Filter = item => showHidden || !item.FileName.StartsWith('.');
        FileViewTree tree = new() { Owner = view };
        FileViewTreeNodeState node = tree.RootNodes.Single();
        TestableFileViewTreeNode treeNode = await RenderNodeAsync(renderer, view, tree, node);
        await renderer.RunAsync(treeNode.ToggleExpandAsync);

        Assert.DoesNotContain(tree.GetVisibleNodesInOrder(), child => child.Item.FileName == ".mobile");
        showHidden = true;
        Assert.Contains(tree.GetVisibleNodesInOrder(), child => child.Item.FileName == ".mobile");
        showHidden = false;
        Assert.DoesNotContain(tree.GetVisibleNodesInOrder(), child => child.Item.FileName == ".mobile");
    }

    [Fact]
    public async Task RootNodes_mirrors_Owner_Items()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        TestableFileView view = await RenderViewAsync(fixture);
        FileViewTree tree = new() { Owner = view };

        IReadOnlyList<FileViewTreeNodeState> roots = tree.RootNodes;

        Assert.Equal(2, roots.Count);
        Assert.Contains(roots, n => n.Item.FileName == "a.txt" && !n.Item.IsDirectory);
        Assert.Contains(roots, n => n.Item.FileName == "Documents" && n.Item.IsDirectory);
    }

    [Fact]
    public async Task RootNodes_preserves_expansion_state_across_a_refresh_by_path()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = await RenderViewAsync(fixture, renderer);
        FileViewTree tree = new() { Owner = view };
        FileViewTreeNodeState firstBuild = tree.RootNodes.Single(n => n.Item.FileName == "Documents");
        firstBuild.IsExpanded = true;
        firstBuild.Children = [];
        FileViewItem firstItemInstance = firstBuild.Item;

        await fixture.CreateFileAsync("/home/user/second.txt");
        await renderer.RunAsync(() => view.RefreshAsync());
        FileViewTreeNodeState secondBuild = tree.RootNodes.Single(n => n.Item.FileName == "Documents");

        Assert.Same(firstBuild, secondBuild);
        Assert.True(secondBuild.IsExpanded);
        Assert.NotNull(secondBuild.Children);
        Assert.NotSame(firstItemInstance, secondBuild.Item);
    }

    [Fact]
    public async Task ToggleExpandAsync_lazily_loads_children_on_first_expand()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        await fixture.CreateFileAsync("/home/user/Documents/report.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = await RenderViewAsync(fixture, renderer);
        FileViewTree tree = new() { Owner = view };
        FileViewTreeNodeState node = tree.RootNodes.Single(n => n.Item.FileName == "Documents");
        TestableFileViewTreeNode treeNode = await RenderNodeAsync(renderer, view, tree, node);

        Assert.False(node.IsExpanded);
        Assert.Null(node.Children);

        await renderer.RunAsync(treeNode.ToggleExpandAsync);

        Assert.True(node.IsExpanded);
        Assert.NotNull(node.Children);
        Assert.Single(node.Children);
        Assert.Equal("report.txt", node.Children[0].Item.FileName);
    }

    [Fact]
    public async Task ToggleExpandAsync_collapses_then_re_expands_without_refetching()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        await fixture.CreateFileAsync("/home/user/Documents/report.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = await RenderViewAsync(fixture, renderer);
        FileViewTree tree = new() { Owner = view };
        FileViewTreeNodeState node = tree.RootNodes.Single(n => n.Item.FileName == "Documents");
        TestableFileViewTreeNode treeNode = await RenderNodeAsync(renderer, view, tree, node);
        await renderer.RunAsync(treeNode.ToggleExpandAsync);
        List<FileViewTreeNodeState> loadedChildren = node.Children!;

        await renderer.RunAsync(treeNode.ToggleExpandAsync);
        Assert.False(node.IsExpanded);
        Assert.Same(loadedChildren, node.Children);

        await fixture.CreateFileAsync("/home/user/Documents/second.txt");
        await renderer.RunAsync(treeNode.ToggleExpandAsync);
        Assert.True(node.IsExpanded);
        Assert.Same(loadedChildren, node.Children);
        Assert.Single(loadedChildren);
    }

    [Fact]
    public async Task ToggleExpandAsync_is_a_noop_for_files()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = await RenderViewAsync(fixture, renderer);
        FileViewTree tree = new() { Owner = view };
        FileViewTreeNodeState node = tree.RootNodes.Single(n => n.Item.FileName == "a.txt");
        TestableFileViewTreeNode treeNode = await RenderNodeAsync(renderer, view, tree, node);

        await renderer.RunAsync(treeNode.ToggleExpandAsync);

        Assert.False(node.IsExpanded);
        Assert.Null(node.Children);
    }

    [Fact]
    public async Task OnRowClick_selects_the_item()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = await RenderViewAsync(fixture, renderer);
        FileViewTree tree = new() { Owner = view };
        FileViewTreeNodeState node = tree.RootNodes.Single(n => n.Item.FileName == "a.txt");
        TestableFileViewTreeNode treeNode = await RenderNodeAsync(renderer, view, tree, node);

        await OnDispatcherAsync(renderer, () => treeNode.OnRowClick(new MouseEventArgs()));

        Assert.True(node.Item.IsSelected);
    }

    [Fact]
    public async Task OnRowDoubleClickAsync_activates_the_item()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = await RenderViewAsync(fixture, renderer);
        FileViewTree tree = new() { Owner = view };
        FileViewTreeNodeState node = tree.RootNodes.Single(n => n.Item.FileName == "Documents");
        TestableFileViewTreeNode treeNode = await RenderNodeAsync(renderer, view, tree, node);

        await renderer.RunAsync(treeNode.OnRowDoubleClickAsync);

        Assert.Equal(VirtualPath.Parse("/home/user/Documents"), view.CurrentDirectory);
    }

    [Fact]
    public async Task OnRowKeyDownAsync_ArrowRight_expands_a_collapsed_directory()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = await RenderViewAsync(fixture, renderer);
        FileViewTree tree = new() { Owner = view };
        FileViewTreeNodeState node = tree.RootNodes.Single(n => n.Item.FileName == "Documents");
        TestableFileViewTreeNode treeNode = await RenderNodeAsync(renderer, view, tree, node);

        await renderer.RunAsync(() => treeNode.OnRowKeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight" }));

        Assert.True(node.IsExpanded);
    }

    [Fact]
    public async Task OnRowKeyDownAsync_ArrowLeft_collapses_an_expanded_directory()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = await RenderViewAsync(fixture, renderer);
        FileViewTree tree = new() { Owner = view };
        FileViewTreeNodeState node = tree.RootNodes.Single(n => n.Item.FileName == "Documents");
        TestableFileViewTreeNode treeNode = await RenderNodeAsync(renderer, view, tree, node);
        await renderer.RunAsync(treeNode.ToggleExpandAsync);

        await renderer.RunAsync(() => treeNode.OnRowKeyDownAsync(new KeyboardEventArgs { Key = "ArrowLeft" }));

        Assert.False(node.IsExpanded);
    }

    [Fact]
    public async Task IsTabStop_is_true_only_for_the_first_root_node_when_nothing_is_selected()
    {
        // Both lowercase to sidestep StringComparer.Ordinal sorting all uppercase letters before lowercase
        // ones (so "Documents" would otherwise sort before "a.txt", surprising anyone reading this test).
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = await RenderViewAsync(fixture, renderer);
        FileViewTree tree = new() { Owner = view };
        FileViewTreeNodeState aNode = tree.RootNodes.Single(n => n.Item.FileName == "a.txt");
        FileViewTreeNodeState bNode = tree.RootNodes.Single(n => n.Item.FileName == "b.txt");
        TestableFileViewTreeNode aTreeNode = await RenderNodeAsync(renderer, view, tree, aNode);
        TestableFileViewTreeNode bTreeNode = await RenderNodeAsync(renderer, view, tree, bNode);

        Assert.True(aTreeNode.IsTabStop());
        Assert.False(bTreeNode.IsTabStop());
    }

    [Fact]
    public async Task OnRowKeyDownAsync_ArrowDown_moves_into_an_expanded_directorys_first_child()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        await fixture.CreateFileAsync("/home/user/Documents/report.txt");
        await fixture.CreateFileAsync("/home/user/z.txt");
        (FakeJSRuntime jsRuntime, FakeJSObjectReference module) = NewFocusJsRuntime();
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = await RenderViewAsync(fixture, renderer, jsRuntime);
        FileViewTree tree = new() { Owner = view };
        FileViewTreeNodeState docsNode = tree.RootNodes.Single(n => n.Item.FileName == "Documents");
        TestableFileViewTreeNode docsTreeNode = await RenderNodeAsync(renderer, view, tree, docsNode);
        await renderer.RunAsync(docsTreeNode.ToggleExpandAsync);

        await renderer.RunAsync(() => docsTreeNode.OnRowKeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" }));

        Assert.True(docsNode.Children!.Single().Item.IsSelected);
        (string Identifier, object?[]? Args) call = module.Calls.Single(c => c.Identifier == "focusItem");
        Assert.Equal("/home/user/Documents/report.txt", call.Args![1]);
    }

    [Fact]
    public async Task OnRowKeyDownAsync_ArrowDown_skips_a_collapsed_directorys_children()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        await fixture.CreateFileAsync("/home/user/Documents/report.txt");
        await fixture.CreateFileAsync("/home/user/z.txt");
        (FakeJSRuntime jsRuntime, _) = NewFocusJsRuntime();
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = await RenderViewAsync(fixture, renderer, jsRuntime);
        FileViewTree tree = new() { Owner = view };
        FileViewTreeNodeState docsNode = tree.RootNodes.Single(n => n.Item.FileName == "Documents");
        TestableFileViewTreeNode docsTreeNode = await RenderNodeAsync(renderer, view, tree, docsNode);

        await renderer.RunAsync(() => docsTreeNode.OnRowKeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" }));

        FileViewItem z = view.Items.Single(i => i.FileName == "z.txt");
        Assert.True(z.IsSelected);
    }

    [Fact]
    public async Task OnRowKeyDownAsync_ArrowUp_at_the_first_root_node_does_nothing()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        (FakeJSRuntime jsRuntime, FakeJSObjectReference module) = NewFocusJsRuntime();
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = await RenderViewAsync(fixture, renderer, jsRuntime);
        FileViewTree tree = new() { Owner = view };
        FileViewTreeNodeState node = tree.RootNodes.Single(n => n.Item.FileName == "a.txt");
        TestableFileViewTreeNode treeNode = await RenderNodeAsync(renderer, view, tree, node);

        await renderer.RunAsync(() => treeNode.OnRowKeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" }));

        Assert.Empty(view.SelectedItems);
        Assert.Empty(module.Calls);
    }

    private static Task<TestableFileView> RenderViewAsync(FileViewTestFixture fixture, IJSRuntime? jsRuntime = null) =>
        RenderViewAsync(fixture, new TestComponentRenderer(EmptyServices()), jsRuntime);

    private static async Task<TestableFileView> RenderViewAsync(
        FileViewTestFixture fixture, TestComponentRenderer renderer, IJSRuntime? jsRuntime = null)
    {
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            InitialDirectory = VirtualPath.Parse("/home/user"),
            JavaScript = jsRuntime ?? new FakeJSRuntime()
        };
        await renderer.RenderAsync(view);
        return view;
    }

    private static Task<TestableFileViewTreeNode> RenderNodeAsync(
        TestComponentRenderer renderer, FileView owner, FileViewTree root, FileViewTreeNodeState node) =>
        renderer.RenderAsync(new TestableFileViewTreeNode { Owner = owner, Root = root, Node = node, Depth = 1 });

    /// <summary>Suppresses the real markup (ShellIcon/HackerIcon) so tests can attach a real RenderHandle and exercise the C# backing directly.</summary>
    private sealed class TestableFileViewTreeNode : FileViewTreeNode
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            // Intentionally empty: these tests exercise the backing logic directly, not rendered markup.
        }
    }
}
