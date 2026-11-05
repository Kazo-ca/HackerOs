using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.FileSystem;
using Kazo.Os.Platform.Core.Tests.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.Tests.FileSystem;

/// <summary>
/// ADR 0100 section 3: protected writes under /etc, /bin and /var (but not /var/log) are written to
/// /var/log/audit.log in the auditd format, allowed or refused, never with their content.
/// </summary>
public sealed class FileSystemAuditTests
{
    [Fact]
    public void Audit_policy_watches_etc_bin_and_var_but_not_var_log_or_home()
    {
        Assert.True(FileSystemAuditPolicy.IsAudited(VirtualPath.Parse("/etc/kazoos/apps.config")));
        Assert.True(FileSystemAuditPolicy.IsAudited(VirtualPath.Parse("/bin")));
        Assert.True(FileSystemAuditPolicy.IsAudited(VirtualPath.Parse("/var/lib/x")));
        Assert.False(FileSystemAuditPolicy.IsAudited(VirtualPath.Parse("/var/log/audit.log")));
        Assert.False(FileSystemAuditPolicy.IsAudited(VirtualPath.Parse("/home/alice/.config/x")));
        Assert.False(FileSystemAuditPolicy.IsAudited(VirtualPath.Parse("/etcetera")));
    }

    [Fact]
    public void Audit_lines_follow_the_auditd_layout_and_parse_back()
    {
        AuditRecord record = new(
            DateTimeOffset.FromUnixTimeMilliseconds(1_792_218_000_123), 42, "write", "/etc/kazoos/apps.config", null,
            "root", "benoit", 231, "settings", "org.kazoos.settings", true, null);

        string line = AuditLineFormat.Format(record);

        Assert.Equal(
            "type=PATH msg=audit(1792218000.123:42): op=write path=\"/etc/kazoos/apps.config\" uid=0 auid=1000 pid=231 comm=\"settings\" app=\"org.kazoos.settings\" res=success UID=\"root\" AUID=\"benoit\"",
            line);
        Assert.True(AuditLineFormat.TryParse(line, out AuditRecord? parsed));
        Assert.Equal(record, parsed);
        Assert.False(AuditLineFormat.TryParse("2026-10-10T03:11:42.123Z INFO  kernel: boot", out _));
    }

    [Fact]
    public async Task Allowed_and_refused_writes_under_etc_reach_audit_log_and_var_log_does_not()
    {
        LogTestFileSystem fs = await LogTestFileSystem.CreateAsync();
        LogFileWriter writer = new(fs.Clock);
        await writer.AttachAsync(fs.Service);
        FileSystemService audited = fs.CreateServiceWithAudit(new FileSystemAuditLog(writer, fs.Clock));

        FileSystemMutationResult created = await audited.CreateAsync(
            new FileSystemCreateRequest(VirtualPath.Parse("/etc/motd"), FileSystemEntryKind.File, FileSystemPermissions.FromMode(0b110_100_100)),
            LogTestFileSystem.SystemContext(fs.Clock.UtcNow));
        FileSystemMutationResult refused = await audited.CreateAsync(
            new FileSystemCreateRequest(VirtualPath.Parse("/etc/evil"), FileSystemEntryKind.File, FileSystemPermissions.FromMode(0b110_100_100)),
            LogTestFileSystem.UserContext(fs.Clock.UtcNow, AppCapabilities.FileSystemSystemWrite));
        await audited.CreateAsync(
            new FileSystemCreateRequest(VirtualPath.Parse("/var/log/other.log"), FileSystemEntryKind.File, FileSystemPermissions.FromMode(0b110_100_100)),
            LogTestFileSystem.SystemContext(fs.Clock.UtcNow));
        await writer.FlushAsync();

        Assert.True(created.Succeeded);
        Assert.False(refused.Succeeded);
        AuditRecord[] records = [.. (await fs.ReadLinesAsync("/var/log/audit.log")).Select(line =>
        {
            Assert.True(AuditLineFormat.TryParse(line, out AuditRecord? record));
            return record;
        })];
        Assert.Equal(2, records.Length);
        Assert.Equal(("create", "/etc/motd", true, "root", AuditLineFormat.UnsetAuid), (records[0].Operation, records[0].Path, records[0].Succeeded, records[0].Uid, records[0].Auid));
        Assert.Equal(("create", "/etc/evil", false, "alice", "alice"), (records[1].Operation, records[1].Path, records[1].Succeeded, records[1].Uid, records[1].Auid));
        Assert.NotNull(records[1].Error);
        FileSystemEntrySnapshot log = (await fs.StatAsync("/var/log/audit.log"))!;
        Assert.Equal(FileSystemPermissions.FromMode(0b110_000_000), log.Metadata.Permissions);
    }

    [Fact]
    public async Task Sudo_writes_record_root_as_uid_and_the_human_as_auid()
    {
        LogTestFileSystem fs = await LogTestFileSystem.CreateAsync();
        RecordingSink sink = new();
        FileSystemService audited = fs.CreateServiceWithAudit(sink);
        FileSystemAuthorizationContext user = LogTestFileSystem.UserContext(fs.Clock.UtcNow, AppCapabilities.FileSystemSystemWrite);
        FileSystemAuthorizationContext sudo = new(
            user.OperationContext with { IsRootElevated = true, ProcessId = 77 }, user.GroupIds, user.EvaluatedAtUtc);

        await audited.CreateAsync(
            new FileSystemCreateRequest(VirtualPath.Parse("/etc/hosts.allow"), FileSystemEntryKind.File, FileSystemPermissions.FromMode(0b110_100_100)),
            sudo);

        (string operation, AppOperationContext context, bool succeeded) = Assert.Single(sink.Records);
        Assert.Equal("create", operation);
        Assert.True(succeeded);
        Assert.True(context.IsRootElevated);
        Assert.Equal("alice", context.UserName);
    }

    private sealed class RecordingSink : IFileSystemAuditSink
    {
        public List<(string Operation, AppOperationContext Context, bool Succeeded)> Records { get; } = [];

        public void Record(string operation, VirtualPath path, VirtualPath? newPath, AppOperationContext context, FileSystemMutationResult result) =>
            Records.Add((operation, context, result.Succeeded));
    }
}
