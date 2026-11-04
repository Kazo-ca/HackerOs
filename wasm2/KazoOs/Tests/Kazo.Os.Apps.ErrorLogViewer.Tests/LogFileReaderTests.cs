using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Xunit;

namespace Kazo.Os.Apps.ErrorLogViewer.Tests;

/// <summary>
/// ADR 0045 section 6: Error Log Viewer reads /var/log with an ordinary app gateway — file rights,
/// no privileged path — and parses lines with <see cref="LogLineFormat"/>.
/// </summary>
public sealed class LogFileReaderTests
{
    [Fact]
    public async Task Lists_syslog_then_app_logs_and_archives_only_on_demand()
    {
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Writer.Write(LogFileTarget.Syslog, DiagnosticSeverity.Information, "kernel", "boot");
        fixture.Writer.Write(LogFileTarget.ForApp("org.b"), DiagnosticSeverity.Information, "org.b", "b");
        fixture.Writer.Write(LogFileTarget.ForApp("org.a"), DiagnosticSeverity.Information, "org.a", "a");
        fixture.Writer.Write(LogFileTarget.AuthLog, DiagnosticSeverity.Information, "session.login", "secret");
        await fixture.Writer.AttachAsync(fixture.Service);
        await fixture.WriteSystemFileAsync("/var/log/syslog.1", "old\n");

        IReadOnlyList<LogSource> active = await fixture.Reader.ListSourcesAsync(includeArchives: false);
        IReadOnlyList<LogSource> all = await fixture.Reader.ListSourcesAsync(includeArchives: true);

        Assert.Equal(["syslog", "apps/org.a.log", "apps/org.b.log"], active.Select(source => source.Label));
        Assert.Equal(["syslog", "syslog.1", "apps/org.a.log", "apps/org.b.log"], all.Select(source => source.Label));
        Assert.True(all[1].IsArchive);
        Assert.DoesNotContain(all, source => source.Label.Contains("auth", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lists_the_audit_log_only_for_administrators_and_parses_its_records()
    {
        // ADR 0100 section 3: audit.log is 0600 root, so it is offered only on request.
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Writer.Write(LogFileTarget.Syslog, DiagnosticSeverity.Information, "kernel", "boot");
        await fixture.Writer.AttachAsync(fixture.Service);
        string refused = AuditLineFormat.Format(new AuditRecord(
            new DateTimeOffset(2026, 10, 17, 8, 30, 0, TimeSpan.Zero), 9, "write", "/etc/hosts", null,
            "alice", "alice", 12, "nano", "org.hackeros.commands.nano", false, "PermissionDenied"));
        await fixture.WriteSystemFileAsync("/var/log/audit.log", refused + "\n");

        IReadOnlyList<LogSource> plain = await fixture.Reader.ListSourcesAsync(includeArchives: false);
        IReadOnlyList<LogSource> admin = await fixture.Reader.ListSourcesAsync(includeArchives: false, includeAudit: true);

        Assert.DoesNotContain(plain, source => source.Label == "audit.log");
        LogSource audit = Assert.Single(admin, source => source.Label == "audit.log");
        Assert.True(audit.RequiresRoot);

        LogRow row = Assert.Single(LogFileReader.Parse(audit, refused + "\n").Rows);
        Assert.Equal(DiagnosticSeverity.Warning, row.Line.Severity);
        Assert.Equal("org.hackeros.commands.nano", row.Line.Source);
        Assert.Contains("/etc/hosts", row.Line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reads_and_parses_lines_counting_foreign_ones()
    {
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Writer.Write(LogFileTarget.Syslog, DiagnosticSeverity.Error, "kernel", "disk\nfailure", 7);
        await fixture.Writer.AttachAsync(fixture.Service);
        LogSource syslog = (await fixture.Reader.ListSourcesAsync(false)).Single();

        LogFileContent content = await fixture.Reader.ReadAsync(syslog);

        LogRow row = Assert.Single(content.Rows);
        Assert.Equal("disk\nfailure", row.Line.Message);
        Assert.Equal(7, row.Line.ProcessId);
        Assert.Null(content.Error);

        LogFileContent mixed = LogFileReader.Parse(syslog, "garbage\n\n2026-10-10T03:11:42.123Z INFO  kernel: ok\n");
        Assert.Single(mixed.Rows);
        Assert.Equal(1, mixed.UnparsedLineCount);
        Assert.Equal(3, mixed.Rows[0].LineNumber);
    }

    [Fact]
    public async Task Without_system_read_the_viewer_sees_nothing()
    {
        Fixture fixture = await Fixture.CreateAsync(capabilities: []);
        fixture.Writer.Write(LogFileTarget.Syslog, DiagnosticSeverity.Information, "kernel", "boot");
        await fixture.Writer.AttachAsync(fixture.Service);

        IReadOnlyList<LogSource> sources = await fixture.Reader.ListSourcesAsync(false);

        Assert.Empty(sources);
    }

    [Fact]
    public async Task Truncate_is_denied_to_an_ordinary_user_and_allowed_after_one_shot_approval()
    {
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Writer.Write(LogFileTarget.Syslog, DiagnosticSeverity.Information, "kernel", "boot");
        await fixture.Writer.AttachAsync(fixture.Service);
        LogSource syslog = (await fixture.Reader.ListSourcesAsync(false)).Single();

        FileSystemMutationResult denied = await fixture.Reader.TruncateAsync(syslog);
        Assert.False(denied.Succeeded);
        FileSystemError raised = Assert.Single(fixture.Denials);
        Assert.NotNull(raised.RequiredCapability);

        // What WindowAppBase does after an administrator approves the prompt (ADR 0042).
        fixture.Operation.OneShotElevation.Grant(raised.RequiredCapability!, raised.Path);
        FileSystemMutationResult approved = await fixture.Reader.TruncateAsync(syslog);

        Assert.True(approved.Succeeded);
        Assert.Empty((await fixture.Reader.ReadAsync(syslog)).Rows);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Reader.TruncateAsync(syslog with { IsArchive = true }));
    }

    private sealed class Fixture
    {
        private int _entryId = 1;
        private int _transactionId = 1000;

        private Fixture(string[] capabilities)
        {
            Clock = new ManualSimulationClock(new DateTimeOffset(2026, 10, 10, 3, 0, 0, TimeSpan.Zero), TimeSpan.FromMilliseconds(100));
            InMemoryFileSystemRepository repository = new(
                () => FileSystemEntryId.FromGuid(new Guid(_entryId++, 0, 0, new byte[8])),
                () => new Guid(_transactionId++, 0, 0, new byte[8]));
            FileSystemMountRouter router = new(repository);
            Service = new FileSystemService(
                router,
                new FileSystemPathResolver(router),
                new FileSystemAuthorizer(),
                new InMemoryTopicMessageBus(new CapabilityGrantRepository(() => Clock.UtcNow)),
                () => new Guid(_transactionId++, 0, 0, new byte[8]));
            Writer = new LogFileWriter(Clock);
            Operation = new AppOperationContext
            {
                AppId = "org.hackeros.error-log-viewer",
                UserId = "alice",
                UserName = "alice",
                UserAuthority = AppAuthority.User,
                GrantedCapabilities = new HashSet<string>(capabilities, StringComparer.Ordinal),
                IsSystemOperation = false,
                OneShotElevation = new OneShotElevationGrantSet()
            };
            Reader = new LogFileReader(new AppFileSystemGateway(
                Service, Clock, Operation, ["users"], notifyPermissionError: Denials.Add));
        }

        public ManualSimulationClock Clock { get; }

        public FileSystemService Service { get; }

        public LogFileWriter Writer { get; }

        public AppOperationContext Operation { get; }

        public LogFileReader Reader { get; }

        public List<FileSystemError> Denials { get; } = [];

        public static async Task<Fixture> CreateAsync(string[]? capabilities = null)
        {
            Fixture fixture = new(capabilities ?? [AppCapabilities.FileSystemSystemRead, AppCapabilities.FileSystemSystemWrite]);
            await new FileSystemSeeder(fixture.Service).SeedAsync("alice", "users");
            return fixture;
        }

        public async Task WriteSystemFileAsync(string path, string content)
        {
            FileSystemAuthorizationContext system = new(
                new AppOperationContext
                {
                    AppId = "org.hackeros.kernel",
                    UserId = "system",
                    UserAuthority = AppAuthority.System,
                    GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
                    IsSystemOperation = true
                },
                ["system"],
                Clock.UtcNow);
            VirtualPath target = VirtualPath.Parse(path);
            await Service.CreateAsync(new FileSystemCreateRequest(target, FileSystemEntryKind.File, FileSystemPermissions.FromMode(0b110_100_100)), system);
            await Service.WriteAsync(new FileSystemWriteRequest(target), new Text(content), system);
        }

        private sealed class Text(string content) : IFileSystemContentSource
        {
            private readonly byte[] _bytes = System.Text.Encoding.UTF8.GetBytes(content);

            public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Text();

            public long? Length => _bytes.LongLength;

            public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
                ValueTask.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
        }
    }
}
