using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Tests.Support;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Kazo.Os.Apps.FileExplorer.Tests;

/// <summary>
/// ADR 0140: Compress and Extract run as HackBackground tasks; without the OS (a unit test) the task
/// runs inline, with the same JSON crossing of its input and result.
/// </summary>
public sealed class FileExplorerZipJobsTests
{
    [Fact]
    public async Task Compress_then_extract_round_trips_a_folder_through_background_tasks()
    {
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway()
            .WithDirectory("/home/u")
            .WithDirectory("/home/u/docs")
            .WithFile("/home/u/docs/a.txt", "alpha")
            .WithFile("/home/u/b.txt", "beta");
        using IDisposable scope = HackExecutionContext.Enter(new MinimalAppExecutionContext(
            AppDefinitionMapper.ToManifest(new FileExplorerApp()), fileSystem: fileSystem));

        string? compressed = await FileExplorerZipService.CompressInBackgroundAsync(
            [new ZipSource("/home/u/docs", "docs", IsDirectory: true), new ZipSource("/home/u/b.txt", "b.txt", IsDirectory: false)],
            VirtualPath.Parse("/home/u/Archive.zip"), new KeyLocalizer(), CancellationToken.None);
        string? extracted = await FileExplorerZipService.ExtractInBackgroundAsync(
            VirtualPath.Parse("/home/u/Archive.zip"), VirtualPath.Parse("/home/u/Archive"), new KeyLocalizer(), CancellationToken.None);
        string? again = await FileExplorerZipService.ExtractInBackgroundAsync(
            VirtualPath.Parse("/home/u/Archive.zip"), VirtualPath.Parse("/home/u/Archive"), new KeyLocalizer(), CancellationToken.None);

        Assert.Null(compressed);
        Assert.Null(extracted);
        Assert.Equal("alpha", await ReadAsync(fileSystem, "/home/u/Archive/docs/a.txt"));
        Assert.Equal("beta", await ReadAsync(fileSystem, "/home/u/Archive/b.txt"));
        // The failure crosses as a text key and its argument, localized by the window.
        Assert.Equal("Error.AlreadyExists(Archive)", again);
    }

    private static async Task<string> ReadAsync(FakeAppFileSystemGateway fileSystem, string path)
    {
        FileSystemResult<FileSystemContentReadHandle> result = await fileSystem.ReadAsync(new FileSystemReadRequest(VirtualPath.Parse(path)));
        Assert.True(result.Succeeded, path);
        await using FileSystemContentReadHandle handle = result.Value!;
        using StreamReader reader = new(handle.Content);
        return await reader.ReadToEndAsync();
    }

    /// <summary>Returns the key, and its arguments in parentheses.</summary>
    private sealed class KeyLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, $"{name}({string.Join(',', arguments)})");

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
