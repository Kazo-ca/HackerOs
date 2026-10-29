using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.FileSystem;
using static HackerOs.Apps.FileExplorer.FileControl.Tests.TestComponentHelpers;

namespace HackerOs.Apps.FileExplorer.FileControl.Tests;

/// <summary>The hover bubble's lines (ADR 0185 §16).</summary>
public sealed class FileViewTooltipTests
{
    [Fact]
    public async Task A_file_shows_its_name_type_size_and_date()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/notes.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);

        string[]? lines = await view.GetItemTooltipAsync("/home/user/notes.txt");

        Assert.NotNull(lines);
        Assert.Equal("notes.txt", lines[0]);
        Assert.Equal("Type: .txt File", lines[1]);
        Assert.StartsWith("Size: ", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("Modified: ", lines[3], StringComparison.Ordinal);
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public async Task A_folder_counts_its_items_and_has_no_size()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Docs");
        await fixture.CreateFileAsync("/home/user/Docs/a.txt");
        await fixture.CreateFileAsync("/home/user/Docs/b.txt");
        await fixture.CreateDirectoryAsync("/home/user/One");
        await fixture.CreateFileAsync("/home/user/One/a.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);

        string[]? docs = await view.GetItemTooltipAsync("/home/user/Docs");
        string[]? one = await view.GetItemTooltipAsync("/home/user/One");

        Assert.Equal(["Docs", "Type: Folder"], docs![..2]);
        Assert.DoesNotContain(docs, line => line.StartsWith("Size", StringComparison.Ordinal));
        Assert.Equal("Contains 2 items", docs[^1]);
        Assert.Equal("Contains 1 item", one![^1]);
    }

    [Fact]
    public async Task An_item_no_longer_listed_has_no_bubble()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };
        await renderer.RenderAsync(view);

        Assert.Null(await view.GetItemTooltipAsync("/home/user/gone.txt"));
    }
}
