using HackerOs.App.Abstractions;
using HackerOs.Apps.FileExplorer.FileControl.Icons;
using HackerOs.Theming.Abstractions.IconPacks;

namespace HackerOs.Apps.FileExplorer.FileControl.Tests;

/// <summary>ADR 0203 §4: the file control draws the pack's images and the built-in icons for the rest.</summary>
public sealed class IconPackShellIconProviderTests
{
    private static IconPackShellIconProvider Provider(params (string Path, string Url)[] images) =>
        new(new IconPack(
            new IconPackInfo("p", "P", null, null, null, "/usr/share/icons/p", IconPackOrigin.System),
            images.ToDictionary(image => image.Path, image => image.Url)));

    private static FileViewIconRequest Request(string path, bool isDirectory, string? extension) =>
        new(VirtualPath.Parse(path), isDirectory, extension, null);

    [Fact]
    public void The_pack_draws_folders_and_known_extensions_as_images()
    {
        IconPackShellIconProvider provider = Provider(("files/folder.png", "data:folder"), ("files/txt.png", "data:txt"));

        Assert.Equal(ShellIconDescriptor.Png("data:folder"), provider.Resolve(Request("/home/a/Docs", true, null)));
        Assert.Equal(ShellIconDescriptor.Png("data:txt"), provider.Resolve(Request("/home/a/a.txt", false, ".txt")));
    }

    [Fact]
    public void Entries_the_pack_does_not_cover_keep_the_built_in_icon()
    {
        IconPackShellIconProvider provider = Provider(("files/folder.png", "data:folder"));

        Assert.Equal(
            new DefaultShellIconProvider().Resolve(Request("/home/a/a.zip", false, ".zip")),
            provider.Resolve(Request("/home/a/a.zip", false, ".zip")));
    }
}
