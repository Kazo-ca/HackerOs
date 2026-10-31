using System.IO.Compression;
using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Apps.FileExplorer.Archives;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Tests.Support;
using Xunit;

namespace HackerOs.Apps.FileExplorer.Tests;

/// <summary>
/// ADR 0185 §6-9: a <c>.zip</c> is listed by the explorer as a read-only folder, its files are read
/// from the archive, a change inside it is refused, and an entry can be extracted out of it.
/// </summary>
public sealed class ZipArchiveBrowsingTests
{
    private const string Archive = "/home/u/photos.zip";

    [Fact]
    public async Task Archive_root_lists_its_entries_with_the_folders_the_zip_only_implies()
    {
        ZipArchiveFileSystemGateway gateway = await CreateGatewayAsync();

        FileSystemResult<FileSystemDirectorySnapshot> root = await gateway.EnumerateAsync(new FileSystemEnumerateRequest(VirtualPath.Parse(Archive)));
        FileSystemResult<FileSystemDirectorySnapshot> year = await gateway.EnumerateAsync(new FileSystemEnumerateRequest(VirtualPath.Parse(Archive + "/2024")));

        Assert.True(root.Succeeded);
        Assert.Equal(["2024", "empty", "readme.txt"], root.Value!.Entries.Select(static entry => entry.Name.Value));
        Assert.Equal(FileSystemEntryKind.Directory, root.Value.Entries[0].Metadata.Kind);
        Assert.Equal(FileSystemEntryKind.Directory, root.Value.Entries[1].Metadata.Kind);
        Assert.True(year.Succeeded);
        Assert.Equal(["a.txt", "trip"], year.Value!.Entries.Select(static entry => entry.Name.Value));
        Assert.Equal(5, Assert.IsType<FileMetadata>(year.Value.Entries[0].Metadata).Length);
    }

    [Fact]
    public async Task A_file_of_the_archive_is_read_and_stated_but_the_archive_itself_stays_a_file()
    {
        ZipArchiveFileSystemGateway gateway = await CreateGatewayAsync();

        FileSystemResult<FileSystemContentReadHandle> read = await gateway.ReadAsync(new FileSystemReadRequest(VirtualPath.Parse(Archive + "/2024/trip/b.txt")));
        FileSystemResult<FileSystemEntrySnapshot> entry = await gateway.StatAsync(new FileSystemStatRequest(VirtualPath.Parse(Archive + "/2024/trip/b.txt")));
        FileSystemResult<FileSystemEntrySnapshot> archive = await gateway.StatAsync(new FileSystemStatRequest(VirtualPath.Parse(Archive)));
        FileSystemResult<FileSystemEntrySnapshot> missing = await gateway.StatAsync(new FileSystemStatRequest(VirtualPath.Parse(Archive + "/nope.txt")));

        Assert.True(read.Succeeded);
        await using (FileSystemContentReadHandle handle = read.Value!)
        {
            Assert.Equal("bravo", await new StreamReader(handle.Content).ReadToEndAsync());
        }

        Assert.Equal(FileSystemEntryKind.File, entry.Value!.Metadata.Kind);
        Assert.Equal("444", entry.Value.Metadata.Permissions.ToString());
        Assert.Equal(FileSystemEntryKind.File, archive.Value!.Metadata.Kind);
        Assert.Equal(FileSystemErrorCode.NotFound, missing.Error!.Code);
    }

    [Fact]
    public async Task Every_change_inside_the_archive_is_refused_but_ordinary_paths_still_change()
    {
        ZipArchiveFileSystemGateway gateway = await CreateGatewayAsync();
        VirtualPath inside = VirtualPath.Parse(Archive + "/2024/new");

        FileSystemMutationResult create = await gateway.CreateAsync(new FileSystemCreateRequest(inside, FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(0b111_101_101)));
        FileSystemMutationResult move = await gateway.MoveAsync(new FileSystemMoveRequest(VirtualPath.Parse(Archive + "/readme.txt"), VirtualPath.Parse("/home/u/readme.txt"), 1, 1, 1));
        FileSystemMutationResult delete = await gateway.DeleteAsync(new FileSystemDeleteRequest(VirtualPath.Parse(Archive + "/readme.txt"), 1, 1));
        FileSystemMutationResult outside = await gateway.CreateAsync(new FileSystemCreateRequest(VirtualPath.Parse("/home/u/new"), FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(0b111_101_101)));

        Assert.Equal(FileSystemErrorCode.ProtectedEntry, create.Transaction.Error!.Code);
        Assert.Equal(FileSystemErrorCode.ProtectedEntry, move.Transaction.Error!.Code);
        Assert.Equal(FileSystemErrorCode.ProtectedEntry, delete.Transaction.Error!.Code);
        Assert.True(outside.Succeeded);
    }

    [Fact]
    public async Task A_folder_named_like_an_archive_stays_an_ordinary_folder()
    {
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway()
            .WithDirectory("/home/u")
            .WithDirectory("/home/u/backup.zip")
            .WithFile("/home/u/backup.zip/notes.txt", "n");
        ZipArchiveFileSystemGateway gateway = new(fileSystem);

        FileSystemResult<FileSystemDirectorySnapshot> listing = await gateway.EnumerateAsync(new FileSystemEnumerateRequest(VirtualPath.Parse("/home/u/backup.zip")));

        Assert.Equal(["notes.txt"], listing.Value!.Entries.Select(static entry => entry.Name.Value));
        Assert.False(gateway.Archives.IsInsideArchive(VirtualPath.Parse("/home/u/backup.zip")));
    }

    [Fact]
    public async Task An_unreadable_archive_is_reported_as_invalid_content()
    {
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway()
            .WithDirectory("/home/u")
            .WithFile("/home/u/broken.zip", "not a zip");
        ZipArchiveFileSystemGateway gateway = new(fileSystem);

        FileSystemResult<FileSystemDirectorySnapshot> listing = await gateway.EnumerateAsync(new FileSystemEnumerateRequest(VirtualPath.Parse("/home/u/broken.zip")));

        Assert.False(listing.Succeeded);
        Assert.Equal(FileSystemErrorCode.InvalidContent, listing.Error!.Code);
    }

    [Fact]
    public async Task Extracting_a_folder_of_the_archive_copies_its_whole_tree()
    {
        FakeAppFileSystemGateway fileSystem = await CreateFileSystemAsync();
        ZipArchiveBrowser browser = new(fileSystem);
        ZipArchiveLocation location = (await browser.LocateAsync(VirtualPath.Parse(Archive + "/2024")))!.Value;

        FileSystemError? error = await browser.ExtractAsync(location, VirtualPath.Parse("/home/u/2024"));

        Assert.Null(error);
        Assert.Equal("alpha", fileSystem.ContentOf("/home/u/2024/a.txt"));
        Assert.Equal("bravo", fileSystem.ContentOf("/home/u/2024/trip/b.txt"));
        Assert.True(browser.IsArchiveEntry(VirtualPath.Parse(Archive + "/2024")));
        Assert.False(browser.IsArchiveEntry(VirtualPath.Parse(Archive)));
        Assert.True(browser.IsInsideArchive(VirtualPath.Parse(Archive)));
    }

    [Fact]
    public void Entry_names_that_climb_out_of_the_archive_are_left_out()
    {
        Assert.Null(ZipArchiveIndex.CleanEntryName("../evil.txt"));
        Assert.Null(ZipArchiveIndex.CleanEntryName("a/../../evil.txt"));
        Assert.Null(ZipArchiveIndex.CleanEntryName("/"));
        Assert.Equal("a/b.txt", ZipArchiveIndex.CleanEntryName("./a\\b.txt"));
        Assert.Equal("a/b.txt", ZipArchiveIndex.CleanEntryName("/a//b.txt"));
    }

    [Fact]
    public void Index_skips_unsafe_entries_and_keeps_the_rest()
    {
        ZipArchiveIndex index = ZipArchiveIndex.TryRead(BuildZip(("../evil.txt", "x"), ("ok.txt", "y")))!;

        Assert.Equal(1, index.Count);
        Assert.True(index.TryGet("ok.txt", out _));
        Assert.Null(ZipArchiveIndex.TryRead(Encoding.UTF8.GetBytes("garbage")));
    }

    [Fact]
    public async Task Entry_ids_stay_the_same_from_one_listing_to_the_next()
    {
        VirtualPath root = VirtualPath.Parse(Archive);

        FileSystemDirectorySnapshot first = (await (await CreateGatewayAsync()).EnumerateAsync(new FileSystemEnumerateRequest(root))).Value!;
        FileSystemDirectorySnapshot second = (await (await CreateGatewayAsync()).EnumerateAsync(new FileSystemEnumerateRequest(root))).Value!;

        Assert.Equal(first.Entries.Select(static entry => entry.Metadata.Id), second.Entries.Select(static entry => entry.Metadata.Id));
        Assert.Equal(first.Entries.Count, first.Entries.Select(static entry => entry.Metadata.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("photos.zip", true)]
    [InlineData("PHOTOS.ZIP", true)]
    [InlineData(".zip", false)]
    [InlineData("photos.tar.gz", false)]
    public void Only_zip_files_open_as_folders(string name, bool expected) =>
        Assert.Equal(expected, ZipArchiveBrowser.IsArchiveName(name));

    private static async Task<ZipArchiveFileSystemGateway> CreateGatewayAsync() => new(await CreateFileSystemAsync());

    private static async Task<FakeAppFileSystemGateway> CreateFileSystemAsync()
    {
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway()
            .WithDirectory("/home/u")
            .WithFile(Archive);
        byte[] zip = BuildZip(("readme.txt", "hello"), ("2024/a.txt", "alpha"), ("2024/trip/b.txt", "bravo"), ("empty/", null));
        FileSystemMutationResult written = await fileSystem.WriteAsync(
            new FileSystemWriteRequest(VirtualPath.Parse(Archive)),
            new MemoryStreamContentSource(FileSystemContentDescriptor.Binary("application/zip"), new MemoryStream(zip)));
        Assert.True(written.Succeeded);
        return fileSystem;
    }

    private static byte[] BuildZip(params (string Name, string? Content)[] entries)
    {
        using MemoryStream buffer = new();
        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string? content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                if (content is not null)
                {
                    using StreamWriter writer = new(entry.Open());
                    writer.Write(content);
                }
            }
        }

        return buffer.ToArray();
    }
}
