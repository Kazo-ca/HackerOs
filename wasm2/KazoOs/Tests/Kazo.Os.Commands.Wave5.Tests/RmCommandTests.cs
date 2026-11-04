using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Rm;
using Kazo.Os.Commands.Trash;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Commands.Wave5.Tests;

/// <summary><c>rm</c> and <c>trash</c> (ADR 0052 section 4).</summary>
public sealed class RmCommandTests
{
    private const string Home = "/home/user";
    private const string Trash = Home + "/.local/share/Trash";

    [Fact]
    public async Task Rm_moves_a_file_to_the_trash_silently()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithFile("/home/user/doomed.txt", "bye");

        (int exitCode, string output, string error) = await RunRmAsync(fs, "doomed.txt");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output + error);
        Assert.False(fs.Exists("/home/user/doomed.txt"));
        Assert.Equal("bye", fs.ContentOf(Trash + "/files/doomed.txt"));
        Assert.Contains("Path=/home/user/doomed.txt", fs.ContentOf(Trash + "/info/doomed.txt.trashinfo"));
    }

    [Fact]
    public async Task Rm_permanent_deletes_without_the_trash()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithFile("/home/user/doomed.txt");

        (int exitCode, _, _) = await RunRmAsync(fs, "--permanent", "doomed.txt");

        Assert.Equal(0, exitCode);
        Assert.False(fs.Exists("/home/user/doomed.txt"));
        Assert.False(fs.Exists(Trash));
    }

    [Fact]
    public async Task Rm_needs_r_for_a_directory_then_trashes_it_whole()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithFile("/home/user/d/a.txt", "a");

        (int refused, _, string error) = await RunRmAsync(fs, "d");
        Assert.Equal(1, refused);
        Assert.Equal("rm: cannot remove 'd': Is a directory", error.Trim());
        Assert.True(fs.Exists("/home/user/d/a.txt"));

        (int exitCode, _, _) = await RunRmAsync(fs, "-rf", "d");
        Assert.Equal(0, exitCode);
        Assert.Equal("a", fs.ContentOf(Trash + "/files/d/a.txt"));
    }

    [Fact]
    public async Task Rm_reports_a_missing_operand_unless_forced()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory(Home);

        (int exitCode, _, string error) = await RunRmAsync(fs, "missing.txt");
        Assert.Equal(1, exitCode);
        Assert.Contains("No such file or directory", error);

        (int forced, _, string forcedError) = await RunRmAsync(fs, "-f", "missing.txt");
        Assert.Equal(0, forced);
        Assert.Equal(string.Empty, forcedError);
    }

    [Fact]
    public async Task Rm_rejects_an_unknown_option()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithFile("/home/user/a.txt");

        (int exitCode, _, string error) = await RunRmAsync(fs, "-x", "a.txt");

        Assert.Equal(1, exitCode);
        Assert.Contains("invalid option -- 'x'", error);
        Assert.True(fs.Exists("/home/user/a.txt"));
    }

    [Fact]
    public async Task Rm_on_a_virtual_filesystem_reports_the_trash_error()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory(Home).WithFile("/proc/uptime");
        CrossMountGateway gateway = new(fs, "/proc/");

        (int exitCode, _, string error) = await RunAsync(new RmCommand(Manifest<RmApp>()), gateway, "/proc/uptime");

        Assert.Equal(1, exitCode);
        Assert.Equal("rm: cannot remove '/proc/uptime': Cannot move to trash: virtual file system", error.Trim());
        Assert.True(fs.Exists("/proc/uptime"));
    }

    [Fact]
    public async Task Rm_permanent_reports_a_concurrent_change()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithFile("/home/user/doomed.txt");
        RaceOnNextMutationFileSystemGateway racing = new(fs, "/home/user/doomed.txt");

        (int exitCode, _, string error) = await RunAsync(new RmCommand(Manifest<RmApp>()), racing, "--permanent", "doomed.txt");

        Assert.Equal(1, exitCode);
        Assert.True(fs.Exists("/home/user/doomed.txt"));
        Assert.Contains("RevisionConflict", error);
    }

    [Fact]
    public async Task Trash_put_list_restore_rm_and_empty()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway()
            .WithFile("/home/user/a.txt", "first").WithFile("/home/user/dir/b.txt", "b");

        Assert.Equal(0, (await RunTrashAsync(fs, "put", "a.txt", "dir")).ExitCode);
        fs.WithFile("/home/user/a.txt", "second");
        Assert.Equal(0, (await RunTrashAsync(fs, "put", "a.txt")).ExitCode);

        (int listed, string listing, _) = await RunTrashAsync(fs, "list");
        Assert.Equal(0, listed);
        string[] lines = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Contains(lines, line => line.EndsWith("  /home/user/a.txt  [a.2.txt]", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.EndsWith("  /home/user/dir", StringComparison.Ordinal));

        // By original path, the most recent deletion is restored ("second" went in as a.2.txt).
        Assert.Equal(0, (await RunTrashAsync(fs, "restore", "/home/user/a.txt")).ExitCode);
        Assert.Equal("second", fs.ContentOf("/home/user/a.txt"));

        // By name, to an explicit destination.
        Assert.Equal(0, (await RunTrashAsync(fs, "restore", "a.txt", "old.txt")).ExitCode);
        Assert.Equal("first", fs.ContentOf("/home/user/old.txt"));

        (int missing, _, string missingError) = await RunTrashAsync(fs, "restore", "nope");
        Assert.Equal(1, missing);
        Assert.Contains("'nope' is not in the trash", missingError);

        Assert.Equal(0, (await RunTrashAsync(fs, "rm", "dir")).ExitCode);
        Assert.False(fs.Exists(Trash + "/files/dir"));

        fs.WithFile("/home/user/c.txt");
        await RunTrashAsync(fs, "put", "c.txt");
        Assert.Equal(0, (await RunTrashAsync(fs, "empty")).ExitCode);
        Assert.Equal(string.Empty, (await RunTrashAsync(fs, "list")).Output);
    }

    [Fact]
    public async Task Trash_without_a_subcommand_prints_usage()
    {
        (int exitCode, _, string error) = await RunTrashAsync(new FakeAppFileSystemGateway().WithDirectory(Home));

        Assert.Equal(1, exitCode);
        Assert.Contains("usage: trash put", error);
    }

    private static AppManifest Manifest<TApp>() where TApp : AppDefinition, new() => AppDefinitionMapper.ToManifest(new TApp());

    private static Task<(int ExitCode, string Output, string Error)> RunRmAsync(IAppFileSystemGateway fs, params string[] arguments) =>
        RunAsync(new RmCommand(Manifest<RmApp>()), fs, arguments);

    private static Task<(int ExitCode, string Output, string Error)> RunTrashAsync(IAppFileSystemGateway fs, params string[] arguments) =>
        RunAsync(new TrashCommand(Manifest<TrashApp>()), fs, arguments);

    /// <summary>Runs a command the way the lifecycle orchestrator does: inside the app's ambient scope, in the shell's directory.</summary>
    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        TerminalAppBase command, IAppFileSystemGateway fs, params string[] arguments)
    {
        MinimalAppExecutionContext app = new(
            command.Manifest,
            fileSystem: fs,
            logging: new FakeAppLoggingGateway(),
            clock: new FakeAppClockGateway(new DateTimeOffset(2026, 10, 10, 14, 3, 27, TimeSpan.Zero)),
            homeDirectory: Home);
        StringWriter output = new();
        StringWriter error = new();
        TerminalExecutionContext context = new(
            app, arguments, new StringReader(string.Empty), output, error, Home, new Dictionary<string, string>());

        using IDisposable scope = KazoExecutionContext.Enter(app, Home);
        int exitCode = await command.ExecuteAsync(context, CancellationToken.None);
        return (exitCode, output.ToString(), error.ToString());
    }

    /// <summary>Answers every move out of <paramref name="virtualPrefix"/> like a move between two mounts.</summary>
    private sealed class CrossMountGateway(FakeAppFileSystemGateway inner, string virtualPrefix) : IAppFileSystemGateway
    {
        public ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(FileSystemReadRequest request, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(request, cancellationToken);

        public ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(FileSystemEnumerateRequest request, CancellationToken cancellationToken = default) =>
            inner.EnumerateAsync(request, cancellationToken);

        public ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(FileSystemStatRequest request, CancellationToken cancellationToken = default) =>
            inner.StatAsync(request, cancellationToken);

        public ValueTask<FileSystemMutationResult> CreateAsync(FileSystemCreateRequest request, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(request, cancellationToken);

        public ValueTask<FileSystemMutationResult> WriteAsync(FileSystemWriteRequest request, IFileSystemContentSource content, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(request, content, cancellationToken);

        public ValueTask<FileSystemMutationResult> MoveAsync(FileSystemMoveRequest request, CancellationToken cancellationToken = default) =>
            request.SourcePath.Value.StartsWith(virtualPrefix, StringComparison.Ordinal)
                ? ValueTask.FromResult(new FileSystemMutationResult(FileSystemTransactionResult.Rejected(
                    Guid.NewGuid(), new FileSystemError(FileSystemOperation.Move, FileSystemErrorCode.CrossMountOperation, request.SourcePath))))
                : inner.MoveAsync(request, cancellationToken);

        public ValueTask<FileSystemMutationResult> CopyAsync(FileSystemCopyRequest request, CancellationToken cancellationToken = default) =>
            inner.CopyAsync(request, cancellationToken);

        public ValueTask<FileSystemMutationResult> DeleteAsync(FileSystemDeleteRequest request, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(request, cancellationToken);

        public ValueTask<FileSystemMutationResult> SetPermissionsAsync(FileSystemSetPermissionsRequest request, CancellationToken cancellationToken = default) =>
            inner.SetPermissionsAsync(request, cancellationToken);

        public IAppFileSystemGateway WithSelectedHandle(FileSystemSelectedResourceHandle handle) => this;
    }
}
