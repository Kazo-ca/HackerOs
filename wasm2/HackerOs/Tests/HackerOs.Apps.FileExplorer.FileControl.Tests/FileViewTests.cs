using HackerOs.App.Abstractions;
using HackerOs.Apps.FileExplorer.FileControl.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using Microsoft.AspNetCore.Components;
using static HackerOs.Apps.FileExplorer.FileControl.Tests.TestComponentHelpers;

namespace HackerOs.Apps.FileExplorer.FileControl.Tests;

/// <summary>
/// Tests for <c>FV-001</c>/<c>FV-002</c>/<c>FV-005</c>/<c>FV-008</c>/<c>FV-009</c> — <see cref="FileView"/>'s
/// C# backing (navigation, selection, inline rename, folder activation, control-initiated create/delete/copy,
/// live-watch wiring, and the cancelable eventing model). No bUnit in this solution: components are attached
/// to a real <see cref="Microsoft.AspNetCore.Components.RenderHandle"/> via <see cref="TestComponentRenderer"/>
/// so <c>StateHasChanged</c>/<c>InvokeAsync</c> behave exactly as in production, while the actual markup
/// (<c>FileViewDetails</c>/<c>FileViewContextMenu</c>, which would otherwise pull in MudBlazor/JSInterop) is
/// suppressed by <see cref="TestableFileView"/> — see <c>doc/en/wasm/dev/old/Global-FileView-And-MessagingSystem/FileViewControl.md</c>.
/// </summary>
public sealed class FileViewTests
{
    [Fact]
    public async Task NavigateAsync_populates_items_and_raises_OnPathChange()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/notes.txt");
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        TestComponentRenderer renderer = new(EmptyServices());
        VirtualPath? raisedPath = null;

        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        view.OnPathChange += (_, e) => raisedPath = e.Path;
        await renderer.RenderAsync(view);

        Assert.Equal(VirtualPath.Parse("/home/user"), view.CurrentDirectory);
        Assert.Equal(2, view.Items.Count);
        Assert.Contains(view.Items, item => item.FileName == "notes.txt" && !item.IsDirectory);
        Assert.Contains(view.Items, item => item.FileName == "Documents" && item.IsDirectory);
        Assert.Equal(VirtualPath.Parse("/home/user"), raisedPath);
    }

    [Fact]
    public async Task NavigateAsync_applies_the_Filter_predicate()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/report.txt");
        await fixture.CreateFileAsync("/home/user/notes.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            InitialDirectory = VirtualPath.Parse("/home/user"),
            Filter = item => item.FileName.Contains("report", StringComparison.Ordinal)
        };
        await renderer.RenderAsync(view);

        Assert.Single(view.Items);
        Assert.Equal("report.txt", view.Items[0].FileName);
    }

    [Fact]
    public async Task RefreshFilter_reapplies_the_current_Filter_without_a_filesystem_round_trip()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/report.txt");
        await fixture.CreateFileAsync("/home/user/notes.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);
        Assert.Equal(2, view.Items.Count);

        view.Filter = item => item.FileName.Contains("report", StringComparison.Ordinal);
        await renderer.RunAsync(() =>
        {
            view.RefreshFilter();
            return Task.CompletedTask;
        });

        Assert.Single(view.Items);
        Assert.Equal("report.txt", view.Items[0].FileName);
    }

    [Fact]
    public async Task RefreshFilter_drops_a_now_filtered_out_item_from_the_selection()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/report.txt");
        await fixture.CreateFileAsync("/home/user/notes.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);
        await OnDispatcherAsync(renderer, () => view.SelectByName("notes.txt"));

        view.Filter = item => item.FileName.Contains("report", StringComparison.Ordinal);
        await renderer.RunAsync(() =>
        {
            view.RefreshFilter();
            return Task.CompletedTask;
        });

        Assert.Empty(view.SelectedItems);
        Assert.Null(view.SelectedItem);
    }

    [Fact]
    public async Task Navigating_event_cancel_prevents_navigation()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);
        view.Navigating += (_, e) => e.Cancel = true;

        await renderer.RunAsync(() => view.NavigateAsync(VirtualPath.Parse("/home/user/Documents")));

        Assert.Equal(VirtualPath.Parse("/home/user"), view.CurrentDirectory);
    }

    [Fact]
    public async Task RefreshAsync_preserves_selection_by_name()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/notes.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);
        await OnDispatcherAsync(renderer, () => view.SelectByName("notes.txt"));

        await fixture.CreateFileAsync("/home/user/second.txt");
        await renderer.RunAsync(() => view.RefreshAsync());

        Assert.Equal(2, view.Items.Count);
        Assert.NotNull(view.SelectedItem);
        Assert.Equal("notes.txt", view.SelectedItem!.FileName);
    }

    [Fact]
    public async Task SelectByName_selects_a_single_item()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/notes.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);

        await OnDispatcherAsync(renderer, () => view.SelectByName("notes.txt"));

        Assert.Single(view.SelectedItems);
        Assert.True(view.SelectedItem!.IsSelected);
    }

    [Fact]
    public async Task Additive_select_extends_the_selection_when_multiselect_is_allowed()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user"), AllowMultiSelect = true };
        await renderer.RenderAsync(view);
        FileViewItem a = view.Items.Single(i => i.FileName == "a.txt");
        FileViewItem b = view.Items.Single(i => i.FileName == "b.txt");

        await renderer.RunAsync(() =>
        {
            a.Select();
            b.Select(additive: true);
            return Task.CompletedTask;
        });

        Assert.Equal(2, view.SelectedItems.Count);
    }

    [Fact]
    public async Task ClearSelect_empties_the_selection()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/notes.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);
        await OnDispatcherAsync(renderer, () => view.SelectByName("notes.txt"));

        await OnDispatcherAsync(renderer, view.ClearSelect);

        Assert.Empty(view.SelectedItems);
        Assert.Null(view.SelectedItem);
    }

    [Fact]
    public async Task SelectionChanging_cancel_prevents_the_selection_from_changing()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/notes.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);
        view.SelectionChanging += (_, e) => e.Cancel = true;

        await OnDispatcherAsync(renderer, () => view.SelectByName("notes.txt"));

        Assert.Empty(view.SelectedItems);
    }

    [Fact]
    public async Task CommitRenameAsync_moves_the_entry_and_raises_moved_then_renamed()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/notes.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);
        FileViewItem item = view.Items.Single(i => i.FileName == "notes.txt");
        List<string> raised = [];
        view.Moved += (_, _) => raised.Add("moved");
        view.Renamed += (_, e) => raised.Add($"renamed:{e.PreviousName}");

        await OnDispatcherAsync(renderer, item.Rename);
        Assert.True(item.IsRenaming);
        await renderer.RunAsync(() => view.CommitRenameAsync(item, "renamed.txt"));

        Assert.Equal(["moved", "renamed:notes.txt"], raised);
        Assert.Contains(view.Items, i => i.FileName == "renamed.txt");
        Assert.DoesNotContain(view.Items, i => i.FileName == "notes.txt");
    }

    [Fact]
    public async Task CommitRenameAsync_cancels_when_the_Renaming_event_cancels()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/notes.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);
        FileViewItem item = view.Items.Single(i => i.FileName == "notes.txt");
        view.Renaming += (_, e) => e.Cancel = true;
        await OnDispatcherAsync(renderer, item.Rename);

        await renderer.RunAsync(() => view.CommitRenameAsync(item, "renamed.txt"));

        Assert.False(item.IsRenaming);
        Assert.Contains(view.Items, i => i.FileName == "notes.txt");
    }

    [Fact]
    public async Task CommitRenameAsync_is_idempotent_when_the_item_is_no_longer_renaming()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/notes.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);
        FileViewItem item = view.Items.Single(i => i.FileName == "notes.txt");
        int renamedCount = 0;
        view.Renamed += (_, _) => renamedCount++;

        // Never entered rename mode (IsRenaming is false) — simulates Enter's keydown handler already
        // having committed before the input's blur handler also fires.
        await renderer.RunAsync(() => view.CommitRenameAsync(item, "renamed.txt"));

        Assert.Equal(0, renamedCount);
        Assert.Contains(view.Items, i => i.FileName == "notes.txt");
    }

    [Fact]
    public async Task CommitRenameAsync_cancels_when_the_proposed_name_is_unchanged_or_blank()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/notes.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);
        FileViewItem item = view.Items.Single(i => i.FileName == "notes.txt");

        await OnDispatcherAsync(renderer, item.Rename);
        await renderer.RunAsync(() => view.CommitRenameAsync(item, "notes.txt"));
        Assert.False(item.IsRenaming);

        await OnDispatcherAsync(renderer, item.Rename);
        await renderer.RunAsync(() => view.CommitRenameAsync(item, "   "));
        Assert.False(item.IsRenaming);
        Assert.Contains(view.Items, i => i.FileName == "notes.txt");
    }

    [Fact]
    public async Task ActivateItemAsync_navigates_into_a_directory_in_Navigate_mode()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            InitialDirectory = VirtualPath.Parse("/home/user"),
            FolderActivation = FileViewFolderActivationMode.Navigate
        };
        await renderer.RenderAsync(view);
        FileViewItem directory = view.Items.Single(i => i.FileName == "Documents");

        await renderer.RunAsync(() => view.ActivateItemAsync(directory));

        Assert.Equal(VirtualPath.Parse("/home/user/Documents"), view.CurrentDirectory);
    }

    [Fact]
    public async Task ActivateItemAsync_throws_in_NewWindow_mode_without_an_Intents_gateway()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            InitialDirectory = VirtualPath.Parse("/home/user"),
            FolderActivation = FileViewFolderActivationMode.NewWindow
        };
        await renderer.RenderAsync(view);
        FileViewItem directory = view.Items.Single(i => i.FileName == "Documents");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            renderer.RunAsync(() => view.ActivateItemAsync(directory)));
    }

    [Fact]
    public async Task ActivateItemAsync_opens_the_directory_as_inode_directory_in_NewWindow_mode()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            InitialDirectory = VirtualPath.Parse("/home/user"),
            FolderActivation = FileViewFolderActivationMode.NewWindow,
            Intents = fixture.Intents
        };
        await renderer.RenderAsync(view);
        FileViewItem directory = view.Items.Single(i => i.FileName == "Documents");

        await renderer.RunAsync(() => view.ActivateItemAsync(directory));

        Assert.Equal([VirtualPath.Parse("/home/user/Documents")], fixture.Intents.OpenFileRequests);
        Assert.Equal(["inode/directory"], fixture.Intents.OpenFileMediaTypes);
    }

    [Fact]
    public async Task ActivateItemAsync_throws_in_Custom_mode_without_a_callback()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            InitialDirectory = VirtualPath.Parse("/home/user"),
            FolderActivation = FileViewFolderActivationMode.Custom
        };
        await renderer.RenderAsync(view);
        FileViewItem directory = view.Items.Single(i => i.FileName == "Documents");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            renderer.RunAsync(() => view.ActivateItemAsync(directory)));
    }

    [Fact]
    public async Task ActivateItemAsync_invokes_the_Custom_callback()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        TestComponentRenderer renderer = new(EmptyServices());
        FileViewItem? activated = null;
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            InitialDirectory = VirtualPath.Parse("/home/user"),
            FolderActivation = FileViewFolderActivationMode.Custom,
            OnCustomFolderActivate = item =>
            {
                activated = item;
                return Task.CompletedTask;
            }
        };
        await renderer.RenderAsync(view);
        FileViewItem directory = view.Items.Single(i => i.FileName == "Documents");

        await renderer.RunAsync(() => view.ActivateItemAsync(directory));

        Assert.Same(directory, activated);
        Assert.Equal(VirtualPath.Parse("/home/user"), view.CurrentDirectory);
    }

    [Fact]
    public async Task Opening_event_cancel_prevents_activation()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            InitialDirectory = VirtualPath.Parse("/home/user"),
            FolderActivation = FileViewFolderActivationMode.Navigate
        };
        await renderer.RenderAsync(view);
        view.Opening += (_, e) => e.Cancel = true;
        FileViewItem directory = view.Items.Single(i => i.FileName == "Documents");

        await renderer.RunAsync(() => view.ActivateItemAsync(directory));

        Assert.Equal(VirtualPath.Parse("/home/user"), view.CurrentDirectory);
    }

    [Fact]
    public async Task DeleteSelectedAsync_deletes_every_selected_item_and_raises_Deleted()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);
        IReadOnlyList<VirtualPath>? deletedPaths = null;
        view.Deleted += (_, e) => deletedPaths = e.Paths;
        await OnDispatcherAsync(renderer, () => view.Items.Single(i => i.FileName == "a.txt").Select());

        await renderer.RunAsync(() => view.DeleteSelectedAsync());

        Assert.Single(view.Items);
        Assert.Equal("b.txt", view.Items[0].FileName);
        Assert.Equal([VirtualPath.Parse("/home/user/a.txt")], deletedPaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteSelectedAsync_hands_the_items_to_DeleteRequested_when_the_host_supplies_it(bool permanent)
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        FileViewDeleteRequestedEventArgs? request = null;
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            InitialDirectory = VirtualPath.Parse("/home/user"),
            DeleteRequested = new EventCallback<FileViewDeleteRequestedEventArgs>(null, (Func<FileViewDeleteRequestedEventArgs, Task>)(e =>
            {
                request = e;
                return Task.CompletedTask;
            }))
        };
        await renderer.RenderAsync(view);
        await OnDispatcherAsync(renderer, () => view.Items.Single(i => i.FileName == "a.txt").Select());

        await renderer.RunAsync(() => view.DeleteSelectedAsync(permanent));

        Assert.NotNull(request);
        Assert.Equal(permanent, request.Permanent);
        Assert.Equal("a.txt", Assert.Single(request.Items).FileName);
        Assert.Single(view.Items);
    }

    [Fact]
    public async Task Deleting_event_cancel_prevents_deletion()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);
        view.Deleting += (_, e) => e.Cancel = true;
        await OnDispatcherAsync(renderer, () => view.Items.Single(i => i.FileName == "a.txt").Select());

        await renderer.RunAsync(() => view.DeleteSelectedAsync());

        Assert.Single(view.Items);
    }

    [Fact]
    public async Task CreateFolderAsync_creates_a_directory_and_raises_Created()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);
        FileViewItem? created = null;
        view.Created += (_, e) => created = e.Item;

        await renderer.RunAsync(() => view.CreateFolderAsync("New Folder"));

        Assert.Contains(view.Items, i => i.FileName == "New Folder" && i.IsDirectory);
        Assert.NotNull(created);
        Assert.Equal("New Folder", created!.FileName);
    }

    [Fact]
    public async Task CreateFolderInteractiveAsync_picks_a_unique_name_selects_and_enters_rename()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/New Folder");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);

        await renderer.RunAsync(() => view.CreateFolderInteractiveAsync());

        FileViewItem created = view.Items.Single(i => i.FileName == "New Folder (2)");
        Assert.Same(created, view.SelectedItem);
        Assert.True(created.IsRenaming);
    }

    [Fact]
    public async Task CopyItemsAsync_copies_into_a_different_directory_and_raises_Copied()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/notes.txt");
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);
        FileViewItem source = view.Items.Single(i => i.FileName == "notes.txt");
        IReadOnlyList<FileViewItem>? copied = null;
        view.Copied += (_, e) => copied = e.Items;

        await renderer.RunAsync(() => view.CopyItemsAsync([source], VirtualPath.Parse("/home/user/Documents")));

        Assert.Equal([source], copied);
        FileSystemResult<FileSystemEntrySnapshot> stat = await fixture.Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/user/Documents/notes.txt")), fixture.UserContext());
        Assert.True(stat.Succeeded);
        // The source directory's own listing (CurrentDirectory) is unaffected by a cross-directory copy.
        Assert.Contains(view.Items, i => i.FileName == "notes.txt");
    }

    [Fact]
    public async Task Copying_event_cancel_prevents_the_copy()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/notes.txt");
        await fixture.CreateDirectoryAsync("/home/user/Documents");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);
        view.Copying += (_, e) => e.Cancel = true;
        FileViewItem source = view.Items.Single(i => i.FileName == "notes.txt");

        await renderer.RunAsync(() => view.CopyItemsAsync([source], VirtualPath.Parse("/home/user/Documents")));

        FileSystemResult<FileSystemEntrySnapshot> stat = await fixture.Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/user/Documents/notes.txt")), fixture.UserContext());
        Assert.False(stat.Succeeded);
    }

    [Fact]
    public async Task A_change_from_another_actor_is_picked_up_through_the_watch_gateway()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            Watch = fixture.Watch,
            InitialDirectory = VirtualPath.Parse("/home/user")
        };
        await renderer.RenderAsync(view);
        Assert.Empty(view.Items);

        await fixture.CreateFileAsync("/home/user/notes.txt");

        bool observed = await WaitUntilAsync(() => view.Items.Any(i => i.FileName == "notes.txt"), TimeSpan.FromSeconds(5));
        Assert.True(observed);
    }

    [Fact]
    public async Task DisposeAsync_stops_the_watch_loop_without_throwing()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            Watch = fixture.Watch,
            InitialDirectory = VirtualPath.Parse("/home/user")
        };
        await renderer.RenderAsync(view);

        await view.DisposeAsync();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        return condition();
    }
}
