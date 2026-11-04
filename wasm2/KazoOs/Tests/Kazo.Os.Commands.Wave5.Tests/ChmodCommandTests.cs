using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Chmod;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Commands.Wave5.Tests;

public sealed class ChmodCommandTests
{
    [Fact]
    public async Task ExecuteAsync_ChangesPermissions_WhenTargetExists()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithFile("/home/user/script.sh", "echo hi");
        TerminalExecutionContext context = CreateContext(fs, "755", "script.sh");

        int exitCode = await new ChmodCommand(AppDefinitionMapper.ToManifest(new ChmodApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsError_WhenTargetMissing()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user");
        TerminalExecutionContext context = CreateContext(fs, "755", "missing.sh");

        int exitCode = await new ChmodCommand(AppDefinitionMapper.ToManifest(new ChmodApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsConflict_WhenTargetRevisionChangesConcurrently()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithFile("/home/user/script.sh", "echo hi");
        RaceOnNextMutationFileSystemGateway racing = new(fs, "/home/user/script.sh");
        TerminalExecutionContext context = new(
            new MinimalAppExecutionContext(AppDefinitionMapper.ToManifest(new ChmodApp()), fileSystem: racing),
            ["755", "script.sh"],
            new StringReader(string.Empty),
            new StringWriter(),
            new StringWriter(),
            "/home/user",
            new Dictionary<string, string>());

        int exitCode = await new ChmodCommand(AppDefinitionMapper.ToManifest(new ChmodApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("RevisionConflict", context.StandardError.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_InvalidMode_ReturnsError()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithFile("/home/user/script.sh");
        TerminalExecutionContext context = CreateContext(fs, "notoctal", "script.sh");

        int exitCode = await new ChmodCommand(AppDefinitionMapper.ToManifest(new ChmodApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    private static TerminalExecutionContext CreateContext(
        FakeAppFileSystemGateway fs, params string[] arguments) => new(
        new MinimalAppExecutionContext(AppDefinitionMapper.ToManifest(new ChmodApp()), fileSystem: fs),
        arguments,
        new StringReader(string.Empty),
        new StringWriter(),
        new StringWriter(),
        "/home/user",
        new Dictionary<string, string>());
}
