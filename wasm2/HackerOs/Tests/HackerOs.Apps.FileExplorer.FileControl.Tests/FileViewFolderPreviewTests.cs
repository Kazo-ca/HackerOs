using HackerOs.App.Abstractions;
using HackerOs.Apps.FileExplorer.FileControl.Icons;
using static HackerOs.Apps.FileExplorer.FileControl.Tests.TestComponentHelpers;

namespace HackerOs.Apps.FileExplorer.FileControl.Tests;

/// <summary>The preview of a folder's content on its icon (ADR 0185 §14).</summary>
public sealed class FileViewFolderPreviewTests
{
    private static async Task<(FileViewTestFixture Fixture, TestableFileView View, TestComponentRenderer Renderer)> ShowHomeAsync(Func<FileViewTestFixture, Task> arrange)
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await arrange(fixture);
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new() { FileSystem = fixture.FileSystem, InitialDirectory = VirtualPath.Parse("/home/user"), Mode = FileViewMode.Icons };
        await renderer.RenderAsync(view);
        return (fixture, view, renderer);
    }

    private static FileViewItem ItemOf(FileView view, string name) => view.Items.Single(item => item.FileName == name);

    [Fact]
    public async Task A_folder_has_no_preview_until_its_tile_was_seen_then_shows_its_first_entries()
    {
        (_, TestableFileView view, _) = await ShowHomeAsync(async fixture =>
        {
            await fixture.CreateDirectoryAsync("/home/user/Docs");
            await fixture.CreateFileAsync("/home/user/Docs/z.txt");
            await fixture.CreateFileAsync("/home/user/Docs/a.png");
            await fixture.CreateFileAsync("/home/user/Docs/.hidden");
            await fixture.CreateFileAsync("/home/user/Docs/m.zip");
            await fixture.CreateFileAsync("/home/user/Docs/n.md");
            await fixture.CreateDirectoryAsync("/home/user/Docs/Sub");
        });

        Assert.Null(view.GetFolderPreview(ItemOf(view, "Docs")));

        await view.LoadFolderPreviewsAsync(["/home/user/Docs"]);

        IReadOnlyList<ShellIconDescriptor>? preview = view.GetFolderPreview(ItemOf(view, "Docs"));
        Assert.NotNull(preview);
        Assert.Equal(["folder", "image", "file-archive", "file-text"], preview.Select(icon => icon.Name));
    }

    [Fact]
    public async Task An_empty_folder_has_an_empty_preview()
    {
        (_, TestableFileView view, _) = await ShowHomeAsync(fixture => fixture.CreateDirectoryAsync("/home/user/Empty"));

        await view.LoadFolderPreviewsAsync(["/home/user/Empty"]);

        Assert.Empty(view.GetFolderPreview(ItemOf(view, "Empty"))!);
    }

    [Fact]
    public async Task A_change_inside_the_folder_is_read_again_after_the_listing_changes()
    {
        (FileViewTestFixture fixture, TestableFileView view, TestComponentRenderer renderer) = await ShowHomeAsync(async fixture =>
        {
            await fixture.CreateDirectoryAsync("/home/user/Docs");
            await fixture.CreateFileAsync("/home/user/Docs/b.txt");
        });
        await view.LoadFolderPreviewsAsync(["/home/user/Docs"]);

        await fixture.CreateDirectoryAsync("/home/user/Docs/A");
        await renderer.RunAsync(() => view.RefreshAsync());
        await view.LoadFolderPreviewsAsync(["/home/user/Docs"]);

        Assert.Equal(["folder", "file-text"], view.GetFolderPreview(ItemOf(view, "Docs"))!.Select(icon => icon.Name));
    }

    [Fact]
    public async Task A_file_never_has_a_preview()
    {
        (_, TestableFileView view, _) = await ShowHomeAsync(fixture => fixture.CreateFileAsync("/home/user/notes.txt"));

        await view.LoadFolderPreviewsAsync(["/home/user/notes.txt"]);

        Assert.Null(view.GetFolderPreview(ItemOf(view, "notes.txt")));
    }

    [Fact]
    public async Task A_file_added_inside_a_previewed_folder_updates_its_preview_through_the_watch()
    {
        FileViewTestFixture fixture = await FileViewTestFixture.CreateAsync();
        await fixture.CreateDirectoryAsync("/home/user/Docs");
        TestComponentRenderer renderer = new(EmptyServices());
        TestableFileView view = new()
        {
            FileSystem = fixture.FileSystem,
            Watch = fixture.Watch,
            InitialDirectory = VirtualPath.Parse("/home/user"),
            Mode = FileViewMode.Icons
        };
        await renderer.RenderAsync(view);
        await view.LoadFolderPreviewsAsync(["/home/user/Docs"]);
        Assert.Empty(view.GetFolderPreview(ItemOf(view, "Docs"))!);

        await fixture.CreateFileAsync("/home/user/Docs/notes.txt");

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && view.GetFolderPreview(ItemOf(view, "Docs")) is not [_])
        {
            await Task.Delay(25);
        }

        Assert.Equal(["file-text"], view.GetFolderPreview(ItemOf(view, "Docs"))!.Select(icon => icon.Name));
        await view.DisposeAsync();
    }
}
