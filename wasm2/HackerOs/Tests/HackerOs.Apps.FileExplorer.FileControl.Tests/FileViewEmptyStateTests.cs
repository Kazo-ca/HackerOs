using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.FileSystem;
using static HackerOs.Apps.FileExplorer.FileControl.Tests.TestComponentHelpers;

namespace HackerOs.Apps.FileExplorer.FileControl.Tests;

/// <summary>The message drawn inside the list when it shows nothing (ADR 0185 §2).</summary>
public sealed class FileViewEmptyStateTests
{
    [Fact]
    public async Task An_empty_folder_says_so_inside_the_list()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Empty");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user/Empty") };

        await renderer.RenderAsync(view);

        Assert.Equal("This folder is empty.", view.EmptyText);
    }

    [Fact]
    public async Task A_filter_hiding_everything_shows_the_host_text_or_a_generic_one()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateFileAsync("/home/user/notes.txt");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            InitialDirectory = VirtualPath.Parse("/home/user"),
            Filter = static _ => false
        };

        await renderer.RenderAsync(view);
        Assert.Equal("Nothing to show here.", view.EmptyText);

        view.NoMatchText = "No results for \"zz\".";
        Assert.Equal("No results for \"zz\".", view.EmptyText);
    }

    [Fact]
    public async Task A_listed_folder_the_tree_and_the_desktop_draw_no_message()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Empty");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user") };

        await renderer.RenderAsync(view);
        Assert.Null(view.EmptyText);

        TestableFileView tree = new()
        {
            FileSystem = fixture.FileSystem,
            InitialDirectory = VirtualPath.Parse("/home/user/Empty"),
            Mode = FileViewMode.Tree
        };
        await renderer.RenderAsync(tree);
        Assert.Null(tree.EmptyText);
    }
}
