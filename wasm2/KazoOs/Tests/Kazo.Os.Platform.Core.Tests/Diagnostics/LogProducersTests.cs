using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Kazo.Os.Platform.Core.Tests.Diagnostics;

/// <summary>Contract tests for the ADR 0045 producer wiring (section 5): ILogger, app logging, audit.</summary>
public sealed class LogProducersTests
{
    [Fact]
    public async Task ILogger_entries_land_in_syslog_redacted_and_in_the_memory_buffer()
    {
        LogTestFileSystem fs = await LogTestFileSystem.CreateAsync();
        BoundedDiagnosticSink memory = new(10);
        LogFileWriter writer = new(fs.Clock, memory);
        using HackerOsDiagnosticLoggerProvider provider = new(memory, writer, fs.Clock);
        ILogger logger = provider.CreateLogger("Kazo.Os.Platform.Core.Boot");

        logger.LogError(new InvalidOperationException("boom"), "Mount failed\nretrying");
        await writer.AttachAsync(fs.Service);

        string line = Assert.Single(await fs.ReadLinesAsync("/var/log/syslog"));
        Assert.True(LogLineFormat.TryParse(line, out LogLine? parsed));
        Assert.Equal(DiagnosticSeverity.Error, parsed.Severity);
        Assert.Equal("Kazo.Os.Platform.Core.Boot", parsed.Source);
        Assert.Equal("Mount failed\nretrying", parsed.Message);
        Assert.Equal(typeof(InvalidOperationException).FullName, parsed.Properties["exceptionType"]);
        Assert.Single(memory.Entries);
    }

    [Fact]
    public async Task App_logging_gateway_writes_its_own_app_log_with_pid_and_redaction()
    {
        LogTestFileSystem fs = await LogTestFileSystem.CreateAsync();
        BoundedDiagnosticSink memory = new(10);
        LogFileWriter writer = new(fs.Clock, memory);
        AppLoggingGateway gateway = new(memory, fs.Clock, "org.hackeros.text-editor", writer, processId: 42);

        gateway.Log(DiagnosticSeverity.Warning, "saved", new Dictionary<string, string> { ["token"] = "s3cret", ["file"] = "a.txt" });
        await writer.AttachAsync(fs.Service);

        string line = Assert.Single(await fs.ReadLinesAsync("/var/log/apps/org.hackeros.text-editor.log"));
        Assert.Equal("2026-10-10T03:11:42.123Z WARN  org.hackeros.text-editor[42]: saved {file=a.txt, token=***redacted***}", line);
        Assert.DoesNotContain("s3cret", line, StringComparison.Ordinal);
        Assert.Null(await fs.StatAsync("/var/log/syslog"));
    }

    [Fact]
    public async Task Audit_entries_land_in_auth_log_redacted_and_stay_in_memory()
    {
        LogTestFileSystem fs = await LogTestFileSystem.CreateAsync();
        LogFileWriter writer = new(fs.Clock);
        FileAuditLog audit = new(writer);

        audit.Record(new AuditEntry(fs.Clock.UtcNow, Guid.NewGuid(), "alice", "session.elevate", "sudo", AuditOutcome.Denied,
            new Dictionary<string, string> { ["password"] = "hunter2" }));
        await writer.AttachAsync(fs.Service);

        string line = Assert.Single(await fs.ReadLinesAsync("/var/log/auth.log"));
        Assert.Equal(
            "2026-10-10T03:11:42.123Z WARN  session.elevate: actor=alice subject=sudo outcome=Denied {password=***redacted***}",
            line);
        AuditEntry kept = Assert.Single(audit.Entries);
        Assert.Equal("***redacted***", kept.Properties["password"]);
    }

    [Fact]
    public async Task Audit_memory_buffer_keeps_the_latest_512_entries()
    {
        LogTestFileSystem fs = await LogTestFileSystem.CreateAsync();
        FileAuditLog audit = new(new LogFileWriter(fs.Clock));

        for (int index = 0; index < FileAuditLog.RecentEntryCapacity + 3; index++)
        {
            audit.Record(new AuditEntry(fs.Clock.UtcNow, Guid.NewGuid(), "alice", "session.login", $"s{index}", AuditOutcome.Success));
        }

        Assert.Equal(FileAuditLog.RecentEntryCapacity, audit.Entries.Count);
        Assert.Equal("s3", audit.Entries[0].Subject);
    }
}
