using System.Text.Json;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk.DragDrop;
using HackerOs.Apps.FileExplorer.FileControl.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using static HackerOs.Apps.FileExplorer.FileControl.Tests.TestComponentHelpers;

namespace HackerOs.Apps.FileExplorer.FileControl.Tests;

/// <summary>
/// Tests for <c>FV-006</c> — <see cref="FileView"/>'s drag-drop orchestration: payload building at drag
/// start, self-drop/non-directory/foreign-drag guards at drop, Ctrl-held copy vs plain move routing (both
/// through the existing cancelable <c>Moving</c>/<c>Copying</c> events), and <see cref="FileView.MoveItemsAsync"/>'s
/// general source-parent resolution (needed because a dragged item may come from a directory this
/// <see cref="FileView"/> instance has never enumerated — e.g. an inter-control drag from another window).
/// The actual native <c>DataTransfer</c> read/write (<c>FileView.razor.js</c>) is stood in for by
/// <see cref="FakeJSRuntime"/>/<see cref="FakeJSObjectReference"/> — there is no real browser in this xunit
/// process, so the JS-side <c>window.event</c> timing assumption itself is unverified here.
/// </summary>
public sealed class FileViewDragDropTests
{
    [Fact]
    public async Task OnItemDragStartAsync_selects_an_unselected_item_and_writes_its_payload()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        (FakeJSRuntime jsRuntime, FakeJSObjectReference module) = NewDragDropRuntime();
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, jsRuntime);
        FileViewItem item = view.Items.Single(i => i.FileName == "a.txt");

        await renderer.RunAsync(() => view.OnItemDragStartAsync(item, new DragEventArgs()));

        Assert.True(item.IsSelected);
        (string Identifier, object?[]? Args) call = module.Calls.Single(c => c.Identifier == "setDragData");
        Assert.Equal(FileView.DragMimeType, call.Args![0]);
        FileViewDragEnvelope envelope = JsonSerializer.Deserialize(
            (string)call.Args[1]!, FileViewDragEnvelopeJsonSerializerContext.Default.FileViewDragEnvelope)!;
        Assert.Single(envelope.Files);
        Assert.Equal("/home/user/a.txt", envelope.Files[0].FilePath);
        Assert.Empty(envelope.Folders);
    }

    [Fact]
    public async Task OnItemDragStartAsync_drags_the_whole_selection_when_the_target_is_already_selected()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        (FakeJSRuntime jsRuntime, FakeJSObjectReference module) = NewDragDropRuntime();
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, jsRuntime);
        FileViewItem a = view.Items.Single(i => i.FileName == "a.txt");
        FileViewItem b = view.Items.Single(i => i.FileName == "b.txt");
        await OnDispatcherAsync(renderer, () =>
        {
            a.Select();
            b.Select(additive: true);
        });

        await renderer.RunAsync(() => view.OnItemDragStartAsync(a, new DragEventArgs()));

        (string Identifier, object?[]? Args) call = module.Calls.Single(c => c.Identifier == "setDragData");
        FileViewDragEnvelope envelope = JsonSerializer.Deserialize(
            (string)call.Args![1]!, FileViewDragEnvelopeJsonSerializerContext.Default.FileViewDragEnvelope)!;
        Assert.Equal(2, envelope.Files.Count);
    }

    [Fact]
    public async Task OnItemDragStartAsync_does_nothing_when_AllowDragDrop_is_false()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        (FakeJSRuntime jsRuntime, _) = NewDragDropRuntime();
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, jsRuntime, allowDragDrop: false);
        FileViewItem item = view.Items.Single(i => i.FileName == "a.txt");
        // The first render loads the module for the hover bubbles (ADR 0185 §16): only the drag matters here.
        jsRuntime.CalledIdentifiers.Clear();

        await renderer.RunAsync(() => view.OnItemDragStartAsync(item, new DragEventArgs()));

        Assert.False(item.IsSelected);
        Assert.Empty(jsRuntime.CalledIdentifiers);
    }

    [Fact]
    public async Task OnItemDropAsync_moves_the_dragged_file_into_the_target_directory()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateDirectoryAsync("/home/user/Target");
        (FakeJSRuntime jsRuntime, FakeJSObjectReference module) = NewDragDropRuntime();
        ConfigureDrop(module, files: [("/home/user/a.txt", "a.txt")]);
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, jsRuntime);
        FileViewItem target = view.Items.Single(i => i.FileName == "Target");
        List<FileViewMovedEventArgs> moved = [];
        view.Moved += (_, e) => moved.Add(e);

        await renderer.RunAsync(() => view.OnItemDropAsync(target, DropArgs(ctrl: false)));

        Assert.False((await fixture.Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/user/a.txt")), fixture.UserContext())).Succeeded);
        Assert.True((await fixture.Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/user/Target/a.txt")), fixture.UserContext())).Succeeded);
        Assert.Single(moved);
        Assert.DoesNotContain(view.Items, i => i.FileName == "a.txt");
    }

    [Fact]
    public async Task OnItemDropAsync_copies_when_Ctrl_is_held()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateDirectoryAsync("/home/user/Target");
        (FakeJSRuntime jsRuntime, FakeJSObjectReference module) = NewDragDropRuntime();
        ConfigureDrop(module, files: [("/home/user/a.txt", "a.txt")]);
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, jsRuntime);
        FileViewItem target = view.Items.Single(i => i.FileName == "Target");
        List<FileViewCopiedEventArgs> copied = [];
        view.Copied += (_, e) => copied.Add(e);

        await renderer.RunAsync(() => view.OnItemDropAsync(target, DropArgs(ctrl: true)));

        Assert.True((await fixture.Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/user/a.txt")), fixture.UserContext())).Succeeded);
        Assert.True((await fixture.Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/user/Target/a.txt")), fixture.UserContext())).Succeeded);
        Assert.Single(copied);
        Assert.Contains(view.Items, i => i.FileName == "a.txt");
    }

    [Fact]
    public async Task OnItemDropAsync_ignores_a_non_directory_target()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateFileAsync("/home/user/b.txt");
        (FakeJSRuntime jsRuntime, FakeJSObjectReference module) = NewDragDropRuntime();
        ConfigureDrop(module, files: [("/home/user/a.txt", "a.txt")]);
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, jsRuntime);
        FileViewItem target = view.Items.Single(i => i.FileName == "b.txt");

        await renderer.RunAsync(() => view.OnItemDropAsync(target, DropArgs(ctrl: false)));

        Assert.DoesNotContain("getDragData", module.CalledIdentifiers);
    }

    [Fact]
    public async Task OnItemDropAsync_ignores_a_drop_without_the_internal_mime_type()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Target");
        (FakeJSRuntime jsRuntime, FakeJSObjectReference module) = NewDragDropRuntime();
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, jsRuntime);
        FileViewItem target = view.Items.Single(i => i.FileName == "Target");
        DragEventArgs args = new() { CtrlKey = false, DataTransfer = new DataTransfer { Types = ["text/plain"] } };

        await renderer.RunAsync(() => view.OnItemDropAsync(target, args));

        Assert.Empty(module.CalledIdentifiers);
    }

    [Fact]
    public async Task OnItemDropAsync_ignores_a_folder_dropped_onto_itself()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Target");
        (FakeJSRuntime jsRuntime, FakeJSObjectReference module) = NewDragDropRuntime();
        ConfigureDrop(module, folders: [("/home/user/Target", "Target")]);
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, jsRuntime);
        FileViewItem target = view.Items.Single(i => i.FileName == "Target");
        bool movingRaised = false;
        view.Moving += (_, _) => movingRaised = true;

        await renderer.RunAsync(() => view.OnItemDropAsync(target, DropArgs(ctrl: false)));

        Assert.False(movingRaised);
    }

    [Fact]
    public async Task Moving_event_cancel_prevents_a_drag_drop_move()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/a.txt");
        await fixture.CreateDirectoryAsync("/home/user/Target");
        (FakeJSRuntime jsRuntime, FakeJSObjectReference module) = NewDragDropRuntime();
        ConfigureDrop(module, files: [("/home/user/a.txt", "a.txt")]);
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture, jsRuntime);
        view.Moving += (_, e) => e.Cancel = true;
        FileViewItem target = view.Items.Single(i => i.FileName == "Target");

        await renderer.RunAsync(() => view.OnItemDropAsync(target, DropArgs(ctrl: false)));

        Assert.True((await fixture.Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/user/a.txt")), fixture.UserContext())).Succeeded);
    }

    [Fact]
    public async Task MoveItemsAsync_moves_an_item_whose_source_parent_is_not_CurrentDirectory()
    {
        // Simulates an inter-control drag: the dragged item lives two levels below this instance's
        // CurrentDirectory (as if it had been dragged from a different FileView/window entirely), so its
        // FileViewItem is built the same way OnItemDropAsync builds one for a foreign path — by re-Statting
        // it — rather than coming from this instance's own Items.
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/SourceDir");
        await fixture.CreateFileAsync("/home/user/SourceDir/note.txt");
        await fixture.CreateDirectoryAsync("/home/user/TargetDir");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderViewAsync(fixture);
        FileSystemResult<FileSystemEntrySnapshot> stat = await fixture.Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/user/SourceDir/note.txt")), fixture.UserContext());
        FileViewItem foreignItem = new(VirtualPath.Parse("/home/user/SourceDir/note.txt"), "note.txt", isDirectory: false, stat.Value!.Metadata)
        {
            Owner = view
        };
        List<FileViewMovedEventArgs> moved = [];
        view.Moved += (_, e) => moved.Add(e);

        await renderer.RunAsync(() => view.MoveItemsAsync([foreignItem], VirtualPath.Parse("/home/user/TargetDir")));

        Assert.False((await fixture.Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/user/SourceDir/note.txt")), fixture.UserContext())).Succeeded);
        Assert.True((await fixture.Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/home/user/TargetDir/note.txt")), fixture.UserContext())).Succeeded);
        Assert.Single(moved);
        // Neither the source's parent nor the destination is this instance's own CurrentDirectory
        // ("/home/user"), so its own Items list correctly does not need to (and does not) change.
        Assert.Contains(view.Items, i => i.FileName == "SourceDir");
        Assert.Contains(view.Items, i => i.FileName == "TargetDir");
    }

    private static (FakeJSRuntime Runtime, FakeJSObjectReference Module) NewDragDropRuntime()
    {
        FakeJSObjectReference module = new();
        module.Handlers["setDragData"] = _ => true;
        FakeJSRuntime runtime = new();
        runtime.Handlers["import"] = _ => module;
        return (runtime, module);
    }

    private static void ConfigureDrop(
        FakeJSObjectReference module,
        IReadOnlyList<(string Path, string Name)>? files = null,
        IReadOnlyList<(string Path, string Name)>? folders = null)
    {
        FileViewDragEnvelope envelope = new(
            [.. (files ?? []).Select(f => new VirtualFileDragPayload(string.Empty, f.Path, f.Name, 0, null))],
            [.. (folders ?? []).Select(f => new VirtualFolderDragPayload(string.Empty, f.Path, f.Name, -1))]);
        string json = JsonSerializer.Serialize(envelope, FileViewDragEnvelopeJsonSerializerContext.Default.FileViewDragEnvelope);
        module.Handlers["getDragData"] = _ => json;
    }

    private static DragEventArgs DropArgs(bool ctrl) => new()
    {
        CtrlKey = ctrl,
        DataTransfer = new DataTransfer { Types = [FileView.DragMimeType] }
    };

    private static Task<(TestComponentRenderer Renderer, TestableFileView View)> RenderViewAsync(
        FileViewTestFixture fixture, IJSRuntime? jsRuntime = null, bool allowDragDrop = true) =>
        RenderViewAsync(fixture, new TestComponentRenderer(EmptyServices()), jsRuntime, allowDragDrop);

    private static async Task<(TestComponentRenderer Renderer, TestableFileView View)> RenderViewAsync(
        FileViewTestFixture fixture, TestComponentRenderer renderer, IJSRuntime? jsRuntime = null, bool allowDragDrop = true)
    {
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            InitialDirectory = VirtualPath.Parse("/home/user"),
            AllowDragDrop = allowDragDrop,
            JavaScript = jsRuntime ?? new FakeJSRuntime()
        };
        await renderer.RenderAsync(view);
        return (renderer, view);
    }
}
