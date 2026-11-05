using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Touch;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Commands.Wave5.Tests;

public sealed class TouchCommandTests
{
    [Fact]
    public async Task ExecuteAsync_CreatesFile_WhenParentExists()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user");
        TerminalExecutionContext context = CreateContext(fs, "newfile.txt");

        int exitCode = await new TouchCommand(AppDefinitionMapper.ToManifest(new TouchApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(fs.Exists("/home/user/newfile.txt"));
    }

    [Fact]
    public async Task ExecuteAsync_ExistingFile_IsBenignNoOp()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithFile("/home/user/existing.txt", "hello");
        TerminalExecutionContext context = CreateContext(fs, "existing.txt");

        int exitCode = await new TouchCommand(AppDefinitionMapper.ToManifest(new TouchApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("hello", fs.ContentOf("/home/user/existing.txt"));
    }

    [Fact]
    public async Task ExecuteAsync_ReportsError_WhenParentMissing()
    {
        FakeAppFileSystemGateway fs = new();
        TerminalExecutionContext context = CreateContext(fs, "newfile.txt");

        int exitCode = await new TouchCommand(AppDefinitionMapper.ToManifest(new TouchApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.False(fs.Exists("/home/user/newfile.txt"));
    }

    [Fact]
    public async Task ExecuteAsync_ReportsConflict_WhenParentRevisionChangesConcurrently()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user");
        RaceOnNextMutationFileSystemGateway racing = new(fs, "/home/user");
        TerminalExecutionContext context = new(
            new MinimalAppExecutionContext(AppDefinitionMapper.ToManifest(new TouchApp()), fileSystem: racing),
            ["newfile.txt"],
            new StringReader(string.Empty),
            new StringWriter(),
            new StringWriter(),
            "/home/user",
            new Dictionary<string, string>());

        int exitCode = await new TouchCommand(AppDefinitionMapper.ToManifest(new TouchApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.False(fs.Exists("/home/user/newfile.txt"));
    }

    private static TerminalExecutionContext CreateContext(
        FakeAppFileSystemGateway fs, params string[] arguments) => new(
        new MinimalAppExecutionContext(AppDefinitionMapper.ToManifest(new TouchApp()), fileSystem: fs),
        arguments,
        new StringReader(string.Empty),
        new StringWriter(),
        new StringWriter(),
        "/home/user",
        new Dictionary<string, string>());
}
