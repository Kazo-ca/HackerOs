using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.DragDrop;
using Kazo.Os.Apps.FileExplorer.FileControl.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using static Kazo.Os.Apps.FileExplorer.FileControl.Tests.TestComponentHelpers;

namespace Kazo.Os.Apps.FileExplorer.FileControl.Tests;

/// <summary>
/// ADR 0058, section 1: drops on the background, hand-off of drops and host files to the host
/// (<see cref="FileView.DropRequested"/>), clipboard shortcuts (<see cref="FileView.ClipboardRequested"/>),
/// dimmed cut items and breadcrumb segments.
/// </summary>
public sealed class FileViewDropAndClipboardTests
{
    [Fact]
    public async Task A_drop_on_the_background_moves_into_the_current_directory()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Sub");
        await fixture.CreateFileAsync("/home/user/Sub/a.txt");
        (FakeJSRuntime runtime, FakeJSObjectReference module) = NewRuntime();
        ConfigureDrop(module, files: ["/home/user/Sub/a.txt"]);
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, runtime);

        await renderer.RunAsync(() => view.OnBackgroundDropAsync(InternalDrop(ctrl: false)));

        Assert.Contains(view.Items, item => item.FileName == "a.txt");
        Assert.False((await fixture.Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/user/Sub/a.txt")), fixture.UserContext())).Succeeded);
    }

    [Fact]
    public async Task With_DropRequested_the_host_receives_the_drop_and_the_view_moves_nothing()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Target");
        await fixture.CreateFileAsync("/home/user/a.txt");
        (FakeJSRuntime runtime, FakeJSObjectReference module) = NewRuntime();
        ConfigureDrop(module, files: ["/home/user/a.txt", "/home/user/missing.txt"], folders: ["/home/user/Target"]);
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, runtime);
        FileViewDropRequestedEventArgs? received = null;
        view.DropRequested = new EventCallback<FileViewDropRequestedEventArgs>(null, (Func<FileViewDropRequestedEventArgs, Task>)(e =>
        {
            received = e;
            return Task.CompletedTask;
        }));
        FileViewItem target = view.Items.Single(item => item.FileName == "Target");

        await renderer.RunAsync(() => view.OnItemDropAsync(target, InternalDrop(ctrl: true)));

        Assert.NotNull(received);
        Assert.Equal(VirtualPath.Parse("/home/user/Target"), received.Destination);
        Assert.True(received.Copy);
        // The missing file and the target folder itself are dropped from the list.
        Assert.Equal([new FileViewDraggedEntry(VirtualPath.Parse("/home/user/a.txt"), false)], received.Entries);
        Assert.Contains(view.Items, item => item.FileName == "a.txt");
    }

    [Fact]
    public async Task Host_files_reach_DropRequested_and_are_released_afterwards()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        (FakeJSRuntime runtime, FakeJSObjectReference module) = NewRuntime();
        module.Handlers["takeDroppedFiles"] = _ => new FileView.DroppedHostFiles(7, [new FileView.DroppedHostFile("photo.png", 3, "image/png")]);
        module.Handlers["releaseDroppedFiles"] = _ => null;
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, runtime);
        FileViewDropRequestedEventArgs? received = null;
        view.DropRequested = new EventCallback<FileViewDropRequestedEventArgs>(null, (Func<FileViewDropRequestedEventArgs, Task>)(e =>
        {
            received = e;
            return Task.CompletedTask;
        }));

        await renderer.RunAsync(() => view.OnBackgroundDropAsync(new DragEventArgs { DataTransfer = new DataTransfer { Types = ["Files"] } }));

        Assert.Equal(VirtualPath.Parse("/home/user"), received!.Destination);
        Assert.Empty(received.Entries);
        Assert.Equal("photo.png", received.HostFiles.Single().Name);
        Assert.Equal("image/png", received.HostFiles.Single().ContentType);
        Assert.DoesNotContain("getDragData", module.CalledIdentifiers);
        (string _, object?[]? args) = module.Calls.Single(call => call.Identifier == "releaseDroppedFiles");
        Assert.Equal(7, args![0]);
    }

    [Fact]
    public async Task Clipboard_shortcuts_go_to_the_host()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, new FakeJSRuntime());
        List<FileViewClipboardRequestedEventArgs> requests = [];
        view.ClipboardRequested = new EventCallback<FileViewClipboardRequestedEventArgs>(null, (Func<FileViewClipboardRequestedEventArgs, Task>)(e =>
        {
            requests.Add(e);
            return Task.CompletedTask;
        }));

        // Nothing selected: Ctrl+C has nothing to copy.
        await renderer.RunAsync(() => view.OnRootKeyDownAsync(new KeyboardEventArgs { Key = "c", CtrlKey = true }));
        await renderer.RunAsync(() => view.OnRootKeyDownAsync(new KeyboardEventArgs { Key = "a", CtrlKey = true }));
        await renderer.RunAsync(() => view.OnRootKeyDownAsync(new KeyboardEventArgs { Key = "x", CtrlKey = true }));
        await renderer.RunAsync(() => view.OnRootKeyDownAsync(new KeyboardEventArgs { Key = "V", CtrlKey = true }));

        Assert.Equal(2, view.SelectedItems.Count);
        Assert.Equal([FileViewClipboardAction.Cut, FileViewClipboardAction.Paste], requests.Select(r => r.Action));
        Assert.Equal(["a.txt", "b.txt"], requests[0].Items.Select(i => i.FileName).Order());
        Assert.Empty(requests[1].Items);
        Assert.Equal(VirtualPath.Parse("/home/user"), requests[1].Destination);
    }

    [Fact]
    public async Task A_paste_event_without_host_files_pastes_the_os_clipboard()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, new FakeJSRuntime());
        List<FileViewClipboardRequestedEventArgs> requests = [];
        view.ClipboardRequested = new EventCallback<FileViewClipboardRequestedEventArgs>(null, (Func<FileViewClipboardRequestedEventArgs, Task>)(e =>
        {
            requests.Add(e);
            return Task.CompletedTask;
        }));

        await renderer.RunAsync(() => view.OnPasteShortcutAsync(hostFiles: false));

        FileViewClipboardRequestedEventArgs request = Assert.Single(requests);
        Assert.Equal(FileViewClipboardAction.Paste, request.Action);
        Assert.Equal(VirtualPath.Parse("/home/user"), request.Destination);
    }

    [Fact]
    public async Task Host_files_pasted_are_copied_into_the_current_directory_like_a_drop()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        (FakeJSRuntime runtime, FakeJSObjectReference module) = NewRuntime();
        module.Handlers["takeDroppedFiles"] = _ => new FileView.DroppedHostFiles(9, [new FileView.DroppedHostFile("notes.txt", 5, "")]);
        module.Handlers["releaseDroppedFiles"] = _ => null;
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, runtime);
        FileViewDropRequestedEventArgs? received = null;
        view.DropRequested = new EventCallback<FileViewDropRequestedEventArgs>(null, (Func<FileViewDropRequestedEventArgs, Task>)(e =>
        {
            received = e;
            return Task.CompletedTask;
        }));

        await renderer.RunAsync(() => view.OnPasteShortcutAsync(hostFiles: true));

        Assert.NotNull(received);
        Assert.Equal(VirtualPath.Parse("/home/user"), received.Destination);
        Assert.True(received.Copy);
        Assert.Empty(received.Entries);
        Assert.Equal("notes.txt", received.HostFiles.Single().Name);
        Assert.Null(received.HostFiles.Single().ContentType);
        (string _, object?[]? args) = module.Calls.Single(call => call.Identifier == "releaseDroppedFiles");
        Assert.Equal(9, args![0]);
    }

    [Fact]
    public async Task Cut_paths_are_dimmed_after_a_refresh()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, new FakeJSRuntime());

        view.CutPaths = ["/home/user/a.txt"];
        await renderer.RunAsync(() => view.RefreshAsync());

        Assert.True(view.Items.Single(i => i.FileName == "a.txt").IsCut);
        Assert.False(view.Items.Single(i => i.FileName == "b.txt").IsCut);
    }

    [Fact]
    public void Breadcrumb_segments_go_from_the_root_down()
    {
        IReadOnlyList<(string Label, VirtualPath Path)> segments = FileViewBreadcrumb.Split(VirtualPath.Parse("/home/benoit/Documents"), "Computer");

        Assert.Equal(["Computer", "home", "benoit", "Documents"], segments.Select(s => s.Label));
        Assert.Equal("/home/benoit", segments[2].Path.Value);
        Assert.Single(FileViewBreadcrumb.Split(VirtualPath.Parse("/"), "/"));
    }

    private static (FakeJSRuntime Runtime, FakeJSObjectReference Module) NewRuntime()
    {
        FakeJSObjectReference module = new();
        FakeJSRuntime runtime = new();
        runtime.Handlers["import"] = _ => module;
        return (runtime, module);
    }

    private static void ConfigureDrop(FakeJSObjectReference module, IReadOnlyList<string>? files = null, IReadOnlyList<string>? folders = null)
    {
        FileViewDragEnvelope envelope = new(
            [.. (files ?? []).Select(path => new VirtualFileDragPayload(string.Empty, path, path[(path.LastIndexOf('/') + 1)..], 0, null))],
            [.. (folders ?? []).Select(path => new VirtualFolderDragPayload(string.Empty, path, path[(path.LastIndexOf('/') + 1)..], -1))]);
        string json = JsonSerializer.Serialize(envelope, FileViewDragEnvelopeJsonSerializerContext.Default.FileViewDragEnvelope);
        module.Handlers["getDragData"] = _ => json;
    }

    private static DragEventArgs InternalDrop(bool ctrl) => new()
    {
        CtrlKey = ctrl,
        DataTransfer = new DataTransfer { Types = [FileView.DragMimeType] }
    };

    private static async Task<(TestComponentRenderer Renderer, TestableFileView View)> RenderViewAsync(FileViewTestFixture fixture, FakeJSRuntime runtime)
    {
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            InitialDirectory = VirtualPath.Parse("/home/user"),
            JavaScript = runtime
        };
        await renderer.RenderAsync(view);
        return (renderer, view);
    }
}
