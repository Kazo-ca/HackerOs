using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions.Diagnostics;

namespace Kazo.Os.Platform.Core.Tests.Diagnostics;

/// <summary>
/// Daemon log files of system apps (ADR 0080, section 3): <c>/var/log/&lt;dir&gt;/&lt;file&gt;.log</c>,
/// written by the platform writer, refused to ordinary apps.
/// </summary>
public sealed class DaemonLogFileTests
{
    [Theory]
    [InlineData("kazoweb/error.log", "/var/log/kazoweb/error.log")]
    [InlineData("kazoweb/docs.access.log", "/var/log/kazoweb/docs.access.log")]
    public void ForDaemon_maps_a_relative_path_under_var_log(string relative, string expected)
    {
        LogFileTarget target = LogFileTarget.ForDaemon(relative);

        Assert.Equal(expected, target.Path.Value);
        Assert.Equal(0b110_100_100, target.Mode);
    }

    [Theory]
    [InlineData("error.log")]
    [InlineData("kazoweb/sub/error.log")]
    [InlineData("kazoweb/error.txt")]
    [InlineData("apps/org.kazoos.browser.log")]
    [InlineData("../etc/passwd.log")]
    [InlineData("kazoweb/../x.log")]
    [InlineData("/kazoweb/error.log")]
    public void ForDaemon_refuses_paths_outside_one_daemon_directory(string relative)
    {
        Assert.ThrowsAny<ArgumentException>(() => LogFileTarget.ForDaemon(relative));
    }

    [Fact]
    public void An_ordinary_app_cannot_write_a_daemon_log()
    {
        LogTestFileSystemClock clock = new();
        AppLoggingGateway gateway = new(new BoundedDiagnosticSink(10), clock.Clock, "org.example.notes");

        Assert.Throws<UnauthorizedAccessException>(() => gateway.AppendToLogFile("kazoweb/error.log", "hello"));
    }

    [Fact]
    public async Task A_system_app_appends_raw_lines_with_line_breaks_escaped()
    {
        LogTestFileSystem fs = await LogTestFileSystem.CreateAsync();
        BoundedDiagnosticSink memory = new(10);
        LogFileWriter writer = new(fs.Clock, memory);
        AppLoggingGateway gateway = new(memory, fs.Clock, "org.kazoos.kazowebd", writer, processId: 7, isSystemApp: true);

        gateway.AppendToLogFile("kazoweb/error.log", "first");
        gateway.AppendToLogFile("kazoweb/error.log", "forged\r\n2026/10/16 [notice] fake");
        await writer.AttachAsync(fs.Service);

        string[] lines = await fs.ReadLinesAsync("/var/log/kazoweb/error.log");
        Assert.Equal(["first", "forged\\n2026/10/16 [notice] fake"], lines);
        Assert.Null(await fs.StatAsync("/var/log/apps/org.kazoos.kazowebd.log"));
    }

    [Fact]
    public void Without_a_writer_lines_stay_in_memory_with_the_file_as_category()
    {
        LogTestFileSystemClock clock = new();
        BoundedDiagnosticSink memory = new(10);
        AppLoggingGateway gateway = new(memory, clock.Clock, "org.kazoos.kazowebd", isSystemApp: true);

        gateway.AppendToLogFile("kazoweb/docs.access.log", "127.0.0.1 - - GET /");

        DiagnosticEntry entry = Assert.Single(memory.Entries);
        Assert.Equal("/var/log/kazoweb/docs.access.log", entry.Category);
        Assert.Equal("127.0.0.1 - - GET /", entry.Message);
    }

    /// <summary>A clock alone, for tests that need no file system.</summary>
    private sealed class LogTestFileSystemClock
    {
        public ManualSimulationClock Clock { get; } = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromMilliseconds(1));
    }
}
