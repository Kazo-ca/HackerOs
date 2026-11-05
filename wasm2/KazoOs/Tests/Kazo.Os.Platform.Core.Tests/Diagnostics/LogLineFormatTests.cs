using Kazo.Os.Simulation.Abstractions.Diagnostics;

namespace Kazo.Os.Platform.Core.Tests.Diagnostics;

/// <summary>Contract tests for the ADR 0045 line format (section 2).</summary>
public sealed class LogLineFormatTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 10, 3, 11, 42, 123, TimeSpan.Zero);

    [Fact]
    public void Format_matches_the_adr_example_exactly()
    {
        string line = LogLineFormat.Format(
            Timestamp,
            DiagnosticSeverity.Warning,
            "org.kazoos.text-editor",
            42,
            "settings: /etc/org.kazoos.text-editor/settings.config:12: invalid value for 'tab-size' (expected int64), using default");

        Assert.Equal(
            "2026-10-10T03:11:42.123Z WARN  org.kazoos.text-editor[42]: settings: /etc/org.kazoos.text-editor/settings.config:12: invalid value for 'tab-size' (expected int64), using default",
            line);
    }

    [Theory]
    [InlineData(DiagnosticSeverity.Trace, "TRACE")]
    [InlineData(DiagnosticSeverity.Debug, "DEBUG")]
    [InlineData(DiagnosticSeverity.Information, "INFO ")]
    [InlineData(DiagnosticSeverity.Warning, "WARN ")]
    [InlineData(DiagnosticSeverity.Error, "ERROR")]
    [InlineData(DiagnosticSeverity.Critical, "CRIT ")]
    public void Level_column_is_padded_to_five_characters_and_round_trips(DiagnosticSeverity severity, string column)
    {
        string line = LogLineFormat.Format(Timestamp, severity, "kernel", null, "hello");

        Assert.Equal($"2026-10-10T03:11:42.123Z {column} kernel: hello", line);
        Assert.True(LogLineFormat.TryParse(line, out LogLine? parsed));
        Assert.Equal(severity, parsed.Severity);
    }

    [Fact]
    public void Timestamp_is_written_in_utc_with_milliseconds()
    {
        DateTimeOffset local = new(2026, 10, 9, 23, 11, 42, 123, TimeSpan.FromHours(-4));

        string line = LogLineFormat.Format(local, DiagnosticSeverity.Information, "kernel", null, "x");

        Assert.StartsWith("2026-10-10T03:11:42.123Z ", line, StringComparison.Ordinal);
        Assert.True(LogLineFormat.TryParse(line, out LogLine? parsed));
        Assert.Equal(local, parsed.TimestampUtc);
        Assert.Equal(TimeSpan.Zero, parsed.TimestampUtc.Offset);
    }

    [Fact]
    public void Multiline_messages_stay_on_one_line_and_round_trip()
    {
        string message = "first line\nsecond\tline\r\nwith a back\\slash and {braces}";

        string line = LogLineFormat.Format(Timestamp, DiagnosticSeverity.Error, "kernel", null, message);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\t', line);
        Assert.Contains(@"first line\nsecond\tline\r\nwith a back\\slash", line, StringComparison.Ordinal);
        Assert.True(LogLineFormat.TryParse(line, out LogLine? parsed));
        Assert.Equal(message, parsed.Message);
        Assert.Empty(parsed.Properties);
    }

    [Fact]
    public void Properties_are_sorted_by_key_and_round_trip_with_delimiters_in_values()
    {
        Dictionary<string, string> properties = new()
        {
            ["zeta"] = "last",
            ["alpha"] = "a, b=c {d}",
            ["path"] = "/var/log/syslog"
        };

        string line = LogLineFormat.Format(Timestamp, DiagnosticSeverity.Information, "kernel", 7, "booted", properties);

        Assert.EndsWith(@" {alpha=a\, b\=c \{d\}, path=/var/log/syslog, zeta=last}", line, StringComparison.Ordinal);
        Assert.True(LogLineFormat.TryParse(line, out LogLine? parsed));
        Assert.Equal("booted", parsed.Message);
        Assert.Equal(7, parsed.ProcessId);
        Assert.Equal(["alpha", "path", "zeta"], parsed.Properties.Keys);
        Assert.Equal("a, b=c {d}", parsed.Properties["alpha"]);
    }

    [Fact]
    public void A_message_that_looks_like_properties_is_not_read_back_as_properties()
    {
        string line = LogLineFormat.Format(Timestamp, DiagnosticSeverity.Information, "kernel", null, "payload {a=b}");

        Assert.True(LogLineFormat.TryParse(line, out LogLine? parsed));
        Assert.Equal("payload {a=b}", parsed.Message);
        Assert.Empty(parsed.Properties);
    }

    [Fact]
    public void Source_whitespace_is_replaced_so_the_header_stays_one_token()
    {
        string line = LogLineFormat.Format(Timestamp, DiagnosticSeverity.Information, "Generic<A, B>", 3, "x");

        Assert.Contains(" Generic<A,_B>[3]: x", line, StringComparison.Ordinal);
        Assert.True(LogLineFormat.TryParse(line, out LogLine? parsed));
        Assert.Equal("Generic<A,_B>", parsed.Source);
        Assert.Equal(3, parsed.ProcessId);
    }

    [Fact]
    public void Diagnostic_entry_overload_uses_the_category_as_source()
    {
        DiagnosticEntry entry = new(Timestamp, DiagnosticSeverity.Debug, "Kazo.Os.Platform.Core.Boot", "ready", Guid.NewGuid());

        Assert.Equal("2026-10-10T03:11:42.123Z DEBUG Kazo.Os.Platform.Core.Boot: ready", LogLineFormat.Format(entry));
    }

    [Theory]
    [InlineData(AuditOutcome.Success, "INFO ")]
    [InlineData(AuditOutcome.Denied, "WARN ")]
    [InlineData(AuditOutcome.Failed, "WARN ")]
    public void Audit_entries_use_the_action_as_source_and_outcome_for_level(AuditOutcome outcome, string level)
    {
        AuditEntry entry = new(Timestamp, Guid.NewGuid(), "alice", "session.elevate", "sudo", outcome,
            new Dictionary<string, string> { ["tty"] = "pts/0" });

        string line = LogLineFormat.FormatAudit(entry);

        Assert.Equal(
            $"2026-10-10T03:11:42.123Z {level} session.elevate: actor=alice subject=sudo outcome={outcome} {{tty=pts/0}}",
            line);
        Assert.True(LogLineFormat.TryParse(line, out LogLine? parsed));
        Assert.Equal("session.elevate", parsed.Source);
        Assert.Null(parsed.ProcessId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a log line")]
    [InlineData("2026-10-10T03:11:42.123Z")]
    [InlineData("2026-10-10T03:11:42.123Z NOPE  kernel: x")]
    [InlineData("2026-10-10T03:11:42.123Z INFO  kernel without colon")]
    [InlineData("2026-10-10 03:11:42 INFO  kernel: x")]
    public void TryParse_rejects_foreign_or_truncated_lines(string? line)
    {
        Assert.False(LogLineFormat.TryParse(line, out LogLine? parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void TryParse_ignores_a_trailing_line_terminator()
    {
        Assert.True(LogLineFormat.TryParse("2026-10-10T03:11:42.123Z INFO  kernel: up\r\n", out LogLine? parsed));
        Assert.Equal("up", parsed.Message);
    }
}
