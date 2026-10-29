using HackerOs.App.Abstractions;
using HackerOs.AppSdk.IO;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Tests.Support;

namespace HackerOs.AppSdk.Tests.IO;

/// <summary>The recursive total size of the file service and the SDK (ADR 0185 §5).</summary>
public sealed class HackDirectorySizeTests
{
    [Fact]
    public async Task GetTotalSize_adds_every_file_below_the_folder()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/game/readme.txt", "12345")
            .WithFile("/home/alice/game/src/main.cs", "1234567890")
            .WithFile("/home/alice/game/src/deep/a.txt", "abc")
            .WithDirectory("/home/alice/game/empty");

        HackDirectorySize size = await HackDirectory.GetTotalSizeAsync("game");
        HackDirectorySize sameFromInfo = await (await HackDirectoryInfo.GetAsync("game")).GetTotalSizeAsync();

        Assert.Equal(new HackDirectorySize(18, 3, 3, true), size);
        Assert.Equal(size, sameFromInfo);
    }

    [Fact]
    public async Task GetTotalSize_of_a_file_is_its_length()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/a.txt", "hello");

        Assert.Equal(new HackDirectorySize(5, 1, 0, true), await HackDirectory.GetTotalSizeAsync("a.txt"));
    }

    [Fact]
    public async Task GetTotalSize_of_nothing_throws_not_found()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway _);

        await Assert.ThrowsAsync<HackDirectoryNotFoundException>(() => HackDirectory.GetTotalSizeAsync("missing"));
    }

    [Fact]
    public async Task A_subfolder_that_cannot_be_listed_is_skipped_and_marks_the_total_incomplete()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/top/a.txt", "1234")
            .WithFile("/home/alice/top/locked/secret.txt", "123456789");

        FileSystemResult<FileSystemTreeSize> result = await FileSystemTreeMeasure.MeasureAsync(
            VirtualPath.Parse("/home/alice/top"),
            (path, token) => fs.StatAsync(new FileSystemStatRequest(path), token),
            (path, token) => path.Value.EndsWith("/locked", StringComparison.Ordinal)
                ? ValueTask.FromResult(FileSystemResult<FileSystemDirectorySnapshot>.Failure(
                    new FileSystemError(FileSystemOperation.Enumerate, FileSystemErrorCode.PermissionDenied, path)))
                : fs.EnumerateAsync(new FileSystemEnumerateRequest(path), token));

        Assert.True(result.Succeeded);
        Assert.Equal(new FileSystemTreeSize(4, 1, 1, false), result.Value);
    }
}
