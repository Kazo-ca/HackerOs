using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.IO;

/// <summary>The recursive total size of the file service and the SDK (ADR 0185 §5).</summary>
public sealed class KazoDirectorySizeTests
{
    [Fact]
    public async Task GetTotalSize_adds_every_file_below_the_folder()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/game/readme.txt", "12345")
            .WithFile("/home/alice/game/src/main.cs", "1234567890")
            .WithFile("/home/alice/game/src/deep/a.txt", "abc")
            .WithDirectory("/home/alice/game/empty");

        KazoDirectorySize size = await KazoDirectory.GetTotalSizeAsync("game");
        KazoDirectorySize sameFromInfo = await (await KazoDirectoryInfo.GetAsync("game")).GetTotalSizeAsync();

        Assert.Equal(new KazoDirectorySize(18, 3, 3, true), size);
        Assert.Equal(size, sameFromInfo);
    }

    [Fact]
    public async Task GetTotalSize_of_a_file_is_its_length()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/a.txt", "hello");

        Assert.Equal(new KazoDirectorySize(5, 1, 0, true), await KazoDirectory.GetTotalSizeAsync("a.txt"));
    }

    [Fact]
    public async Task GetTotalSize_of_nothing_throws_not_found()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway _);

        await Assert.ThrowsAsync<KazoDirectoryNotFoundException>(() => KazoDirectory.GetTotalSizeAsync("missing"));
    }

    [Fact]
    public async Task A_subfolder_that_cannot_be_listed_is_skipped_and_marks_the_total_incomplete()
    {
        using IDisposable scope = KazoTestHost.Enter(out FakeAppFileSystemGateway fs);
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
