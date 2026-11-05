using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Alias;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Commands.Wave5.Tests;

public sealed class AliasCommandTests
{
    [Fact]
    public async Task ExecuteAsync_SetThenList_PersistsAcrossSeparateInvocations()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user");

        int setExitCode = await Run(fs, "myalias=echo hi");
        Assert.Equal(0, setExitCode);

        string listing = await RunCapturingOutput(fs, []);
        Assert.Contains("alias myalias='echo hi'", listing);
    }

    [Fact]
    public async Task ExecuteAsync_LookupExistingAlias_PrintsIt()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user");
        await Run(fs, "ll=ls -la");

        string output = await RunCapturingOutput(fs, "ll");

        Assert.Contains("alias ll='ls -la'", output);
    }

    [Fact]
    public async Task ExecuteAsync_LookupUnknownAlias_ReportsNotFound()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user");

        TerminalExecutionContext context = CreateContext(fs, "missing");
        int exitCode = await new AliasCommand(AppDefinitionMapper.ToManifest(new AliasApp())).ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("not found", context.StandardError.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_RemoveFlag_ActuallyRemovesTheAlias()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user");
        await Run(fs, "ll=ls -la");

        int removeExitCode = await Run(fs, "-r", "ll");
        Assert.Equal(0, removeExitCode);

        string listing = await RunCapturingOutput(fs, []);
        Assert.DoesNotContain("ll=", fs.ContentOf("/home/user/.kazoos/aliases"));
        Assert.Equal(string.Empty, listing);
    }

    [Fact]
    public async Task ExecuteAsync_RemoveUnknownAlias_ReportsNotFound()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user");

        TerminalExecutionContext context = CreateContext(fs, "-r", "missing");
        int exitCode = await new AliasCommand(AppDefinitionMapper.ToManifest(new AliasApp())).ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("not found", context.StandardError.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_ReportsFailure_WhenSaveRacesAConcurrentChange()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user");
        await Run(fs, "ll=ls -la");

        RaceOnNextMutationFileSystemGateway racing = new(fs, "/home/user/.kazoos/aliases");
        TerminalExecutionContext context = new(
            new MinimalAppExecutionContext(AppDefinitionMapper.ToManifest(new AliasApp()), fileSystem: racing),
            ["ll=ls -la --color"],
            new StringReader(string.Empty),
            new StringWriter(),
            new StringWriter(),
            "/home/user",
            new Dictionary<string, string> { ["HOME"] = "/home/user" });

        int exitCode = await new AliasCommand(AppDefinitionMapper.ToManifest(new AliasApp())).ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("failed to save", context.StandardError.ToString());
    }

    private static async Task<int> Run(FakeAppFileSystemGateway fs, params string[] arguments)
    {
        TerminalExecutionContext context = CreateContext(fs, arguments);
        return await new AliasCommand(AppDefinitionMapper.ToManifest(new AliasApp())).ExecuteAsync(context, CancellationToken.None);
    }

    private static async Task<string> RunCapturingOutput(FakeAppFileSystemGateway fs, params string[] arguments)
    {
        TerminalExecutionContext context = CreateContext(fs, arguments);
        await new AliasCommand(AppDefinitionMapper.ToManifest(new AliasApp())).ExecuteAsync(context, CancellationToken.None);
        return context.StandardOutput.ToString()!;
    }

    private static TerminalExecutionContext CreateContext(
        FakeAppFileSystemGateway fs, params string[] arguments) => new(
        new MinimalAppExecutionContext(AppDefinitionMapper.ToManifest(new AliasApp()), fileSystem: fs),
        arguments,
        new StringReader(string.Empty),
        new StringWriter(),
        new StringWriter(),
        "/home/user",
        new Dictionary<string, string> { ["HOME"] = "/home/user" });
}
