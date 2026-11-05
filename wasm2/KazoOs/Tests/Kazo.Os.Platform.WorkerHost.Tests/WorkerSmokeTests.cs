namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>A command runs in a loopback worker through the real shell, orchestrator and protocol.</summary>
public sealed class WorkerSmokeTests
{
    [Fact]
    public async Task A_command_runs_in_a_worker_and_its_output_reaches_the_shell()
    {
        await using OsFixture os = await OsFixture.CreateAsync(workers: true);

        ShellRun where = await os.RunAsync("whereami").WaitAsync(TimeSpan.FromSeconds(10));
        ShellRun echo = await os.RunAsync("echo hello worker").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, where.ExitCode);
        Assert.StartsWith("worker pid=", where.Stdout, StringComparison.Ordinal);
        Assert.Contains("cwd=/home/alice user=alice", where.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, echo.ExitCode);
        Assert.Equal("hello worker\n", echo.Stdout);
    }
}
