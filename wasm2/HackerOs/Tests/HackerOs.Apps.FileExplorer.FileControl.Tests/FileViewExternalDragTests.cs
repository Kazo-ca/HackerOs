using System.IO.Compression;
using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.FileSystem;
using Microsoft.AspNetCore.Components.Web;
using static HackerOs.Apps.FileExplorer.FileControl.Tests.TestComponentHelpers;

namespace HackerOs.Apps.FileExplorer.FileControl.Tests;

/// <summary>Dragging items out of the browser to download them (ADR 0185 §4-5).</summary>
public sealed class FileViewExternalDragTests
{
    [Fact]
    public async Task A_press_on_a_file_prepares_the_file_as_it_is()
    {
        (FileViewTestFixture fixture, FakeJSObjectReference module) = await ArrangeAsync();
        await WriteFileAsync(fixture, "/home/user/a.txt", "hello");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderAsync(fixture, module);

        await renderer.RunAsync(() => view.PrepareExternalDragAsync("/home/user/a.txt", []));

        object?[] args = module.Calls.Single(call => call.Identifier == "prepareDownload").Args!;
        Assert.Equal("/home/user/a.txt", args[0]);
        Assert.Equal("a.txt", args[1]);
        Assert.Equal("hello", Encoding.UTF8.GetString((byte[])args[3]!));
    }

    [Fact]
    public async Task A_press_on_a_folder_prepares_a_zip_of_its_content()
    {
        (FileViewTestFixture fixture, FakeJSObjectReference module) = await ArrangeAsync();
        await fixture.CreateDirectoryAsync("/home/user/Docs");
        await WriteFileAsync(fixture, "/home/user/Docs/note.txt", "note");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderAsync(fixture, module);

        await renderer.RunAsync(() => view.PrepareExternalDragAsync("/home/user/Docs", []));

        object?[] args = module.Calls.Single(call => call.Identifier == "prepareDownload").Args!;
        Assert.Equal("Docs.zip", args[1]);
        Assert.Equal("application/zip", args[2]);
        using ZipArchive archive = new(new MemoryStream((byte[])args[3]!), ZipArchiveMode.Read);
        Assert.Equal("Docs/note.txt", Assert.Single(archive.Entries).FullName);
    }

    [Fact]
    public async Task A_press_on_a_selected_item_prepares_the_whole_selection_and_an_unselected_one_only_itself()
    {
        (FileViewTestFixture fixture, FakeJSObjectReference module) = await ArrangeAsync();
        await WriteFileAsync(fixture, "/home/user/a.txt", "a");
        await WriteFileAsync(fixture, "/home/user/b.txt", "b");
        await WriteFileAsync(fixture, "/home/user/c.txt", "c");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderAsync(fixture, module);
        string[] selection = ["/home/user/a.txt", "/home/user/b.txt"];

        await renderer.RunAsync(() => view.PrepareExternalDragAsync("/home/user/b.txt", selection));
        await renderer.RunAsync(() => view.PrepareExternalDragAsync("/home/user/c.txt", selection));

        List<object?[]> prepared = [.. module.Calls.Where(call => call.Identifier == "prepareDownload").Select(call => call.Args!)];
        Assert.Equal("/home/user/a.txt\n/home/user/b.txt", prepared[0][0]);
        Assert.Equal("Archive.zip", prepared[0][1]);
        Assert.Equal("/home/user/c.txt", prepared[1][0]);
        Assert.Equal("c.txt", prepared[1][1]);
    }

    [Fact]
    public async Task A_second_press_on_the_same_unchanged_items_keeps_what_was_prepared()
    {
        (FileViewTestFixture fixture, FakeJSObjectReference module) = await ArrangeAsync();
        await WriteFileAsync(fixture, "/home/user/a.txt", "a");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderAsync(fixture, module);

        await renderer.RunAsync(() => view.PrepareExternalDragAsync("/home/user/a.txt", []));
        await renderer.RunAsync(() => view.PrepareExternalDragAsync("/home/user/a.txt", []));

        Assert.Single(module.Calls, call => call.Identifier == "prepareDownload");
    }

    [Fact]
    public async Task Items_over_the_limit_are_not_prepared_and_their_drag_shows_the_bubble()
    {
        (FileViewTestFixture fixture, FakeJSObjectReference module) = await ArrangeAsync();
        await WriteFileAsync(fixture, "/home/user/big.bin", "0123456789");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderAsync(fixture, module);
        view.ExternalDragLimitBytes = 4;
        FileViewItem item = view.Items.Single(i => i.FileName == "big.bin");

        await renderer.RunAsync(() => view.PrepareExternalDragAsync(item.FullPath.Value, []));
        await renderer.RunAsync(() => view.OnItemDragStartAsync(item, new DragEventArgs()));

        Assert.DoesNotContain(module.Calls, call => call.Identifier == "prepareDownload");
        Assert.Contains(module.Calls, call => call.Identifier == "clearPreparedDownload");
        object?[] drag = module.Calls.Single(call => call.Identifier == "setDragData").Args!;
        Assert.Equal("/home/user/big.bin", drag[2]);
        Assert.False(string.IsNullOrEmpty(drag[3] as string));
    }

    [Fact]
    public async Task A_drag_within_the_limit_carries_the_key_without_a_bubble()
    {
        (FileViewTestFixture fixture, FakeJSObjectReference module) = await ArrangeAsync();
        await WriteFileAsync(fixture, "/home/user/a.txt", "a");
        (TestComponentRenderer renderer, TestableFileView view) = await RenderAsync(fixture, module);
        FileViewItem item = view.Items.Single(i => i.FileName == "a.txt");

        await renderer.RunAsync(() => view.PrepareExternalDragAsync(item.FullPath.Value, []));
        await renderer.RunAsync(() => view.OnItemDragStartAsync(item, new DragEventArgs()));

        object?[] drag = module.Calls.Single(call => call.Identifier == "setDragData").Args!;
        Assert.Equal("/home/user/a.txt", drag[2]);
        Assert.Null(drag[3]);
    }

    private static async Task<(FileViewTestFixture Fixture, FakeJSObjectReference Module)> ArrangeAsync()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        FakeJSObjectReference module = new();
        module.Handlers["setDragData"] = _ => true;
        module.Handlers["prepareDownload"] = _ => true;
        module.Handlers["clearPreparedDownload"] = _ => null;
        return (fixture, module);
    }

    private static async Task<(TestComponentRenderer Renderer, TestableFileView View)> RenderAsync(FileViewTestFixture fixture, FakeJSObjectReference module)
    {
        FakeJSRuntime runtime = new();
        runtime.Handlers["import"] = _ => module;
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

    private static async Task WriteFileAsync(FileViewTestFixture fixture, string path, string content)
    {
        await fixture.CreateFileAsync(path);
        FileSystemMutationResult written = await fixture.FileSystem.WriteAsync(new FileSystemWriteRequest(VirtualPath.Parse(path)), new TextSource(content));
        Assert.Equal(FileSystemTransactionStatus.Committed, written.Transaction.Status);
    }

    private sealed class TextSource(string content) : IFileSystemContentSource
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(content);

        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Text();

        public long? Length => _bytes.LongLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
    }
}
