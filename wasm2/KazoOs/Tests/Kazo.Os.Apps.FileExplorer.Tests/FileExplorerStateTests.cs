using Kazo.Os.Apps.FileExplorer.FileControl;
using Kazo.Os.Apps.FileExplorer;
using Xunit;

namespace Kazo.Os.Apps.FileExplorer.Tests;

/// <summary>
/// Tests for <see cref="FileExplorerState"/>'s remaining responsibility after the Phase 4 migration onto
/// <c>FileView</c> (<c>INT-001</c>): navigation history only. Sorting and selection are no longer this
/// class's concern — <c>FileView.SelectedItems</c> and its own Details-mode sort own those now, so the
/// tests that used to cover <c>SortColumn</c>/<c>SortAscending</c>/<c>SelectedItemNames</c> here were
/// deleted rather than adapted, per <c>ADR 0037</c>'s "delete rather than keep a parallel path".
/// </summary>
public sealed class FileExplorerStateTests
{
    [Fact]
    public void FileExplorerState_manages_navigation_history_stacks()
    {
        FileExplorerState state = new("/home/user");
        Assert.Equal("/home/user", state.CurrentPath);
        Assert.False(state.CanNavigateBack);
        Assert.False(state.CanNavigateForward);
        Assert.True(state.CanNavigateUp);

        state.NavigateTo("/home/user/docs");
        Assert.Equal("/home/user/docs", state.CurrentPath);
        Assert.True(state.CanNavigateBack);

        state.NavigateUp();
        Assert.Equal("/home/user", state.CurrentPath);

        state.NavigateBack();
        Assert.Equal("/home/user/docs", state.CurrentPath);

        state.NavigateTo("/var/log");
        Assert.Equal("/var/log", state.CurrentPath);

        state.NavigateBack();
        Assert.Equal("/home/user/docs", state.CurrentPath);
        Assert.True(state.CanNavigateForward);

        state.NavigateForward();
        Assert.Equal("/var/log", state.CurrentPath);
    }

    [Fact]
    public void NavigateTo_the_current_path_is_a_no_op()
    {
        FileExplorerState state = new("/home/user");
        int changeCount = 0;
        state.StateChanged += () => changeCount++;

        state.NavigateTo("/home/user");

        Assert.Equal(0, changeCount);
        Assert.False(state.CanNavigateBack);
    }

    [Fact]
    public void NavigateTo_clears_the_forward_stack()
    {
        FileExplorerState state = new("/home/user");
        state.NavigateTo("/home/user/docs");
        state.NavigateBack();
        Assert.True(state.CanNavigateForward);

        state.NavigateTo("/var/log");

        Assert.False(state.CanNavigateForward);
    }

    [Fact]
    public void ViewMode_defaults_to_Details_and_is_freely_settable()
    {
        FileExplorerState state = new("/home/user");
        Assert.Equal(FileViewMode.Details, state.ViewMode);

        state.ViewMode = FileViewMode.Tree;

        Assert.Equal(FileViewMode.Tree, state.ViewMode);
    }
}
