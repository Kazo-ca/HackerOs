using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Apps;
using HackerOs.Commands.Df;
using HackerOs.Commands.Mount;
using HackerOs.Commands.Umount;
using HackerOs.Tests.Support;
using Xunit;

namespace HackerOs.Commands.Tests;

/// <summary><c>mount</c>, <c>umount</c> and <c>df</c> with network drives (ADR 0181, section 12).</summary>
public sealed class MountCommandTests
{
    private const string ProcMounts =
        "hda / idbfs rw,relatime 0 0\n"
        + "proc /proc proc rw,nosuid,nodev,noexec 0 0\n"
        + "host:My\\040Files /mnt/My\\040Files hostfs rw,nosuid,nodev 0 0\n";

    [Fact]
    public async Task Mount_without_arguments_lists_proc_mounts_like_util_linux()
    {
        Run run = await RunAsync(new MountApp(), static manifest => new MountCommand(manifest), new FakeAppDriveGateway());

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(
            [
                "hda on / type idbfs (rw,relatime)",
                "proc on /proc type proc (rw,nosuid,nodev,noexec)",
                "host:My Files on /mnt/My Files type hostfs (rw,nosuid,nodev)"
            ],
            run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Mount_t_hostfs_mounts_the_chosen_folder_at_a_relative_point()
    {
        FakeAppDriveGateway drives = new() { NextPick = "Projects" };

        Run run = await RunAsync(new MountApp(), static manifest => new MountCommand(manifest), drives, "-t", "hostfs", "../../mnt/code");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("Projects mounted on /mnt/code", run.Stdout.Trim());
        Assert.Contains(drives.Drives, static drive => drive.Name == "/mnt/code");
    }

    [Fact]
    public async Task A_closed_dialog_or_an_unsupported_browser_fails_with_32()
    {
        Run closed = await RunAsync(new MountApp(), static manifest => new MountCommand(manifest), new FakeAppDriveGateway(), "-t", "hostfs");
        Run unsupported = await RunAsync(
            new MountApp(), static manifest => new MountCommand(manifest), new FakeAppDriveGateway { Supported = false, NextPick = "x" }, "-t", "hostfs");

        Assert.Equal(32, closed.ExitCode);
        Assert.Contains("no folder chosen", closed.Stderr, StringComparison.Ordinal);
        Assert.Equal(32, unsupported.ExitCode);
        Assert.Contains("not supported", unsupported.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "--bogus")]
    [InlineData(1, "-o", "ro", "/mnt/x")]
    [InlineData(32, "-t", "nfs", "/mnt/x")]
    public async Task Mount_refuses_what_it_cannot_do(int exitCode, params string[] arguments)
    {
        Run run = await RunAsync(new MountApp(), static manifest => new MountCommand(manifest), new FakeAppDriveGateway(), arguments);

        Assert.Equal(exitCode, run.ExitCode);
        Assert.NotEmpty(run.Stderr);
    }

    [Fact]
    public async Task Remount_asks_the_browser_again_and_reports_a_refusal()
    {
        FakeAppDriveGateway drives = new FakeAppDriveGateway().WithNetworkDrive("/mnt/work", "work", ready: false);

        Run granted = await RunAsync(new MountApp(), static manifest => new MountCommand(manifest), drives, "-o", "remount", "/mnt/work");
        drives.GrantOnReconnect = false;
        Run refused = await RunAsync(new MountApp(), static manifest => new MountCommand(manifest), drives, "-o", "remount", "/mnt/work");

        Assert.Equal(0, granted.ExitCode);
        Assert.Equal(32, refused.ExitCode);
        Assert.Contains("permission refused", refused.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Umount_removes_the_drive_and_reports_one_that_is_not_mounted()
    {
        FakeAppDriveGateway drives = new FakeAppDriveGateway().WithNetworkDrive("/mnt/work", "work");

        Run removed = await RunAsync(new UmountApp(), static manifest => new UmountCommand(manifest), drives, "/mnt/work");
        Run missing = await RunAsync(new UmountApp(), static manifest => new UmountCommand(manifest), drives, "/mnt/work");
        Run usage = await RunAsync(new UmountApp(), static manifest => new UmountCommand(manifest), drives);

        Assert.Equal(0, removed.ExitCode);
        Assert.DoesNotContain(drives.Drives, static drive => drive.Name == "/mnt/work");
        Assert.Equal(32, missing.ExitCode);
        Assert.Contains("not mounted", missing.Stderr, StringComparison.Ordinal);
        Assert.Equal(1, usage.ExitCode);
    }

    [Fact]
    public async Task Df_shows_a_network_drive_without_sizes()
    {
        FakeAppDriveGateway drives = new FakeAppDriveGateway().WithNetworkDrive("/mnt/work", "work");

        Run run = await RunAsync(new DfApp(), static manifest => new DfCommand(manifest), drives);

        Assert.Equal(0, run.ExitCode);
        string line = run.Stdout.Split('\n').Single(static line => line.StartsWith("host:work", StringComparison.Ordinal));
        Assert.Equal(["host:work", "-", "-", "-", "-", "/mnt/work"], line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static async Task<Run> RunAsync(
        AppDefinition definition,
        Func<AppManifest, TerminalAppBase> command,
        FakeAppDriveGateway drives,
        params string[] arguments)
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(definition);
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway()
            .WithFile("/proc/mounts", ProcMounts)
            .WithDirectory("/home/alice");
        MinimalAppExecutionContext app = new(
            manifest, fileSystem: fileSystem, homeDirectory: "/home/alice",
            environment: new FakeAppEnvironmentGateway("/home/alice"), drives: drives);
        StringWriter stdout = new();
        StringWriter stderr = new();
        TerminalExecutionContext context = new(
            app, arguments, new StringReader(string.Empty), stdout, stderr,
            "/home/alice", new Dictionary<string, string> { ["HOME"] = "/home/alice" });

        using IDisposable scope = HackExecutionContext.Enter(app);
        int exitCode = await command(manifest).ExecuteAsync(context, CancellationToken.None);
        return new Run(exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr);
}
