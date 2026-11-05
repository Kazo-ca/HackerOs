using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Diff;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Commands.Wave5.Tests;

public sealed class DiffCommandTests
{
    [Fact]
    public async Task ExecuteAsync_IdenticalFiles_ReturnsZeroWithNoOutput()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway()
            .WithFile("/home/user/a.txt", "one\ntwo\nthree\n")
            .WithFile("/home/user/b.txt", "one\ntwo\nthree\n");

        int exitCode = await Run(fs, "a.txt", "b.txt");

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task ExecuteAsync_SingleInsertedLine_DoesNotCascadeIntoTrailingLines()
    {
        // A naive index-by-index comparison would report every line after the insertion point
        // as "changed" instead of a single clean insertion — this is the bug being fixed.
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway()
            .WithFile("/home/user/a.txt", "one\ntwo\nthree\n")
            .WithFile("/home/user/b.txt", "one\nINSERTED\ntwo\nthree\n");

        string output = await RunCapturingOutput(fs, "a.txt", "b.txt");

        Assert.Contains("> INSERTED", output);
        Assert.DoesNotContain("< two", output);
        Assert.DoesNotContain("< three", output);
        Assert.DoesNotContain("> two", output);
        Assert.DoesNotContain("> three", output);
    }

    [Fact]
    public async Task ExecuteAsync_SingleDeletedLine_ReportsOnlyThatLine()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway()
            .WithFile("/home/user/a.txt", "one\nREMOVED\ntwo\nthree\n")
            .WithFile("/home/user/b.txt", "one\ntwo\nthree\n");

        string output = await RunCapturingOutput(fs, "a.txt", "b.txt");

        Assert.Contains("< REMOVED", output);
        Assert.DoesNotContain("< two", output);
        Assert.DoesNotContain("< three", output);
    }

    [Fact]
    public async Task ExecuteAsync_ChangedLine_ReportsClassicChangeHunk()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway()
            .WithFile("/home/user/a.txt", "one\ntwo\nthree\n")
            .WithFile("/home/user/b.txt", "one\nTWO\nthree\n");

        string output = await RunCapturingOutput(fs, "a.txt", "b.txt");

        Assert.Contains("2c2", output);
        Assert.Contains("< two", output);
        Assert.Contains("---", output);
        Assert.Contains("> TWO", output);
    }

    [Fact]
    public async Task ExecuteAsync_UnifiedMode_GroupsSingleInsertionIntoOneHunk()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway()
            .WithFile("/home/user/a.txt", "one\ntwo\nthree\n")
            .WithFile("/home/user/b.txt", "one\nINSERTED\ntwo\nthree\n");

        string output = await RunCapturingOutput(fs, "-u", "a.txt", "b.txt");
        string[] hunkHeaders = output.Split('\n').Where(l => l.StartsWith("@@")).ToArray();

        Assert.Single(hunkHeaders);
        Assert.Contains("+INSERTED", output);
    }

    private static async Task<int> Run(FakeAppFileSystemGateway fs, params string[] arguments)
    {
        TerminalExecutionContext context = CreateContext(fs, arguments);
        return await new DiffCommand(AppDefinitionMapper.ToManifest(new DiffApp())).ExecuteAsync(context, CancellationToken.None);
    }

    private static async Task<string> RunCapturingOutput(FakeAppFileSystemGateway fs, params string[] arguments)
    {
        TerminalExecutionContext context = CreateContext(fs, arguments);
        await new DiffCommand(AppDefinitionMapper.ToManifest(new DiffApp())).ExecuteAsync(context, CancellationToken.None);
        return context.StandardOutput.ToString()!;
    }

    private static TerminalExecutionContext CreateContext(
        FakeAppFileSystemGateway fs, params string[] arguments) => new(
        new MinimalAppExecutionContext(AppDefinitionMapper.ToManifest(new DiffApp()), fileSystem: fs),
        arguments,
        new StringReader(string.Empty),
        new StringWriter(),
        new StringWriter(),
        "/home/user",
        new Dictionary<string, string>());
}
