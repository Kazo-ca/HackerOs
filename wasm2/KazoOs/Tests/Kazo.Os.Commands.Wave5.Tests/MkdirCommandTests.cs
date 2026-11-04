using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Mkdir;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Commands.Wave5.Tests;

public sealed class MkdirCommandTests
{
    [Fact]
    public async Task ExecuteAsync_CreatesDirectory_WhenParentExists()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user");
        TerminalExecutionContext context = CreateContext(fs, "newdir");

        int exitCode = await new MkdirCommand(AppDefinitionMapper.ToManifest(new MkdirApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(fs.Exists("/home/user/newdir"));
    }

    [Fact]
    public async Task ExecuteAsync_ReportsError_WhenParentMissing()
    {
        FakeAppFileSystemGateway fs = new();
        TerminalExecutionContext context = CreateContext(fs, "newdir");

        int exitCode = await new MkdirCommand(AppDefinitionMapper.ToManifest(new MkdirApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.False(fs.Exists("/home/user/newdir"));
    }

    [Fact]
    public async Task ExecuteAsync_ReportsConflict_WhenParentRevisionChangesConcurrently()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user");
        RaceOnNextMutationFileSystemGateway racing = new(fs, "/home/user");
        TerminalExecutionContext context = new(
            new MinimalAppExecutionContext(AppDefinitionMapper.ToManifest(new MkdirApp()), fileSystem: racing),
            ["newdir"],
            new StringReader(string.Empty),
            new StringWriter(),
            new StringWriter(),
            "/home/user",
            new Dictionary<string, string>());

        int exitCode = await new MkdirCommand(AppDefinitionMapper.ToManifest(new MkdirApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.False(fs.Exists("/home/user/newdir"));
        Assert.Contains("RevisionConflict", Text(context.StandardError));
    }

    [Fact]
    public async Task ExecuteAsync_MissingOperand_ReturnsError()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user");
        TerminalExecutionContext context = CreateContext(fs);

        int exitCode = await new MkdirCommand(AppDefinitionMapper.ToManifest(new MkdirApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsFileExists_WithoutParentsOption()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user").WithDirectory("/home/user/docs");
        TerminalExecutionContext context = CreateContext(fs, "docs");

        int exitCode = await Run(context);

        Assert.Equal(1, exitCode);
        Assert.Equal("mkdir: cannot create directory 'docs': File exists", Text(context.StandardError).TrimEnd());
    }

    [Theory]
    [InlineData("-p")]
    [InlineData("--parents")]
    public async Task ExecuteAsync_Parents_CreatesEveryMissingComponent(string option)
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user");
        TerminalExecutionContext context = CreateContext(fs, option, "a/b/c");

        int exitCode = await Run(context);

        Assert.Equal(0, exitCode);
        Assert.True(fs.Exists("/home/user/a"));
        Assert.True(fs.Exists("/home/user/a/b"));
        Assert.True(fs.Exists("/home/user/a/b/c"));
        Assert.Empty(Text(context.StandardError));
    }

    [Fact]
    public async Task ExecuteAsync_Parents_AcceptsAnExistingDirectory()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user").WithDirectory("/home/user/a");
        TerminalExecutionContext context = CreateContext(fs, "-p", "a", "/home/user");

        int exitCode = await Run(context);

        Assert.Equal(0, exitCode);
        Assert.Empty(Text(context.StandardError));
    }

    [Fact]
    public async Task ExecuteAsync_Parents_FailsOnAFileInThePath()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user").WithFile("/home/user/notes", "x");
        TerminalExecutionContext context = CreateContext(fs, "-p", "notes/sub", "notes");

        int exitCode = await Run(context);

        Assert.Equal(1, exitCode);
        Assert.False(fs.Exists("/home/user/notes/sub"));
        string[] errors = Text(context.StandardError).TrimEnd().Split('\n');
        Assert.Equal("mkdir: cannot create directory 'notes/sub': Not a directory", errors[0].TrimEnd());
        Assert.Equal("mkdir: cannot create directory 'notes': File exists", errors[1].TrimEnd());
    }

    [Fact]
    public async Task ExecuteAsync_ParentsVerbose_PrintsEachCreatedDirectory()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user").WithDirectory("/home/user/a");
        TerminalExecutionContext context = CreateContext(fs, "-pv", "a/b/c");

        int exitCode = await Run(context);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            ["mkdir: created directory 'a/b'", "mkdir: created directory 'a/b/c'"],
            Text(context.StandardOutput).TrimEnd().Split('\n').Select(line => line.TrimEnd()));
    }

    [Fact]
    public async Task ExecuteAsync_DotDotOperand_IsNormalized()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user").WithDirectory("/home/other");
        TerminalExecutionContext context = CreateContext(fs, "../other/x");

        int exitCode = await Run(context);

        Assert.Equal(0, exitCode);
        Assert.True(fs.Exists("/home/other/x"));
    }

    [Fact]
    public async Task ExecuteAsync_UnknownOption_IsRejected()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/user");
        TerminalExecutionContext context = CreateContext(fs, "-z", "dir");

        int exitCode = await Run(context);

        Assert.Equal(1, exitCode);
        Assert.False(fs.Exists("/home/user/dir"));
        Assert.Equal("mkdir: invalid option -- 'z'", Text(context.StandardError).TrimEnd());
    }

    private static string Text(TextWriter writer) => writer.ToString() ?? string.Empty;

    private static Task<int> Run(TerminalExecutionContext context) =>
        new MkdirCommand(AppDefinitionMapper.ToManifest(new MkdirApp()))
            .ExecuteAsync(context, CancellationToken.None).AsTask();

    private static TerminalExecutionContext CreateContext(
        FakeAppFileSystemGateway fs, params string[] arguments) => new(
        new MinimalAppExecutionContext(AppDefinitionMapper.ToManifest(new MkdirApp()), fileSystem: fs),
        arguments,
        new StringReader(string.Empty),
        new StringWriter(),
        new StringWriter(),
        "/home/user",
        new Dictionary<string, string>());
}
