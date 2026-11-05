using System.Collections.Immutable;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.Commands.Kazobackup;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Xunit;

namespace Kazo.Os.Commands.Wave5.Tests;

/// <summary><c>kazobackup</c> (ADR 0099, section 1): arguments, root requirement and the SDK calls.</summary>
public sealed class KazobackupCommandTests
{
    [Fact]
    public async Task Without_arguments_it_prints_the_usage_and_exits_2()
    {
        (int exit, string _, string stderr) = await RunAsync(root: true, new FakeBackup());

        Assert.Equal(2, exit);
        Assert.Contains("usage: kazobackup create | restore [--merge|--replace]", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("create", "--replace")]
    public async Task An_unknown_command_or_option_exits_2(params string[] args)
    {
        FakeBackup backup = new();

        (int exit, _, _) = await RunAsync(root: true, backup, args);

        Assert.Equal(2, exit);
        Assert.Equal(0, backup.Calls);
    }

    [Fact]
    public async Task Without_sudo_it_refuses_before_touching_the_backup()
    {
        FakeBackup backup = new();

        (int exit, _, string stderr) = await RunAsync(root: false, backup, "create");

        Assert.Equal(1, exit);
        Assert.Contains("must be run as root (try: sudo kazobackup create)", stderr, StringComparison.Ordinal);
        Assert.Equal(0, backup.Calls);
    }

    [Fact]
    public async Task Create_reports_the_downloaded_archive()
    {
        (int exit, string stdout, _) = await RunAsync(root: true, new FakeBackup(), "create");

        Assert.Equal(0, exit);
        Assert.Contains("kazoos-20261017-071200Z.kosbak", stdout, StringComparison.Ordinal);
        Assert.Contains("12 records", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_merges_by_default_and_a_closed_picker_restores_nothing()
    {
        FakeBackup backup = new() { PickNothing = true };

        (int exit, _, string stderr) = await RunAsync(root: true, backup, "restore");

        Assert.Equal(1, exit);
        Assert.Equal(KazoBackupRestoreMode.Merge, backup.LastMode);
        Assert.Contains("no file chosen", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_replace_passes_the_mode_and_an_invalid_archive_exits_1()
    {
        FakeBackup backup = new() { Invalid = true };

        (int exit, _, string stderr) = await RunAsync(root: true, backup, "restore", "--replace");

        Assert.Equal(1, exit);
        Assert.Equal(KazoBackupRestoreMode.Replace, backup.LastMode);
        Assert.Contains("not a usable backup", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_without_backup_says_so()
    {
        (int exit, _, string stderr) = await RunAsync(root: true, new FakeBackup { Supported = false }, "create");

        Assert.Equal(1, exit);
        Assert.Contains("cannot back up", stderr, StringComparison.Ordinal);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(bool root, FakeBackup backup, params string[] args)
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new KazobackupApp());
        StringWriter stdout = new();
        StringWriter stderr = new();
        TerminalExecutionContext context = new(
            app: new BackupContext(manifest, root, backup),
            arguments: args.ToImmutableArray(),
            standardInput: TextReader.Null,
            standardOutput: stdout,
            standardError: stderr,
            workingDirectory: "/root",
            environment: ImmutableDictionary<string, string>.Empty);
        int exit = await new KazobackupCommand(manifest).ExecuteAsync(context, CancellationToken.None);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private sealed class FakeBackup : IAppSystemBackupGateway
    {
        public bool Supported { get; init; } = true;

        public bool PickNothing { get; init; }

        public bool Invalid { get; init; }

        public int Calls { get; private set; }

        public KazoBackupRestoreMode? LastMode { get; private set; }

        public bool IsSupported => Supported;

        public ValueTask<KazoBackupInfo> CreateAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new KazoBackupInfo(
                "kazoos-20261017-071200Z.kosbak", 4096, 12, new DateTimeOffset(2026, 10, 17, 7, 12, 0, TimeSpan.Zero)));
        }

        public ValueTask<KazoBackupInfo?> RestoreAsync(KazoBackupRestoreMode mode, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastMode = mode;
            return Invalid
                ? throw new KazoBackupInvalidException("not a backup")
                : ValueTask.FromResult<KazoBackupInfo?>(PickNothing ? null : new KazoBackupInfo("old.kosbak", 10, 1, DateTimeOffset.UnixEpoch));
        }
    }

    private sealed class BackupContext(AppManifest manifest, bool root, IAppSystemBackupGateway backup) : IAppExecutionContext
    {
        public AppManifest Manifest { get; } = manifest;
        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId => "user";
        public AppAuthority UserAuthority => AppAuthority.Administrator;
        public bool IsRootElevated => root;
        public IAppSystemBackupGateway SystemBackup => backup;
        public IReadOnlySet<string> GrantedCapabilities => new HashSet<string>(Manifest.Capabilities);
        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
        public ProcessId ProcessId { get; } = ProcessId.FromInt64(1);
        public CancellationToken CancellationToken => CancellationToken.None;
        public ICapabilityChecker Capabilities => throw new NotImplementedException();
        public IAppFileSystemGateway FileSystem => throw new NotImplementedException();
        public IAppEventGateway Events => throw new NotImplementedException();
        public IAppNotificationGateway Notifications => throw new NotImplementedException();
        public IAppLoggingGateway Logging => throw new NotImplementedException();
        public IAppClockGateway Clock => throw new NotImplementedException();
        public IAppProcessGateway Processes => throw new NotImplementedException();
    }
}
