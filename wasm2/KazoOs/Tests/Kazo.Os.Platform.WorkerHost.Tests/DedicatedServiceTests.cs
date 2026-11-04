using Kazo.Os.Simulation.Abstractions.Workers;

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>
/// ADR 0070, sections 1 and 3: a service runs in a worker of its own, taken from the ready worker;
/// <c>systemctl stop</c> reaches its <c>StopRequested</c> and <c>OnStoppingAsync</c> in the worker, and
/// the worker is freed when the service ends.
/// </summary>
public sealed class DedicatedServiceTests
{
    [Fact]
    public async Task A_service_runs_in_a_dedicated_worker_and_stops_cleanly_through_systemctl()
    {
        await using OsFixture os = await WorkerPoolTests.StartedAsync();

        ShellRun start = await os.RunAsync("systemctl start org.test.ticker").WaitAsync(TimeSpan.FromSeconds(10));
        await OsFixture.WaitUntilAsync(() => os.FindPid("org.test.ticker") is not null);
        long pid = os.FindPid("org.test.ticker")!.Value;
        await OsFixture.WaitUntilAsync(() => os.Pool!.GetWorkers().Any(w => w.Role == WorkerRole.Dedicated && w.ProcessIds.Contains(pid)));
        ShellRun active = await os.RunAsync("systemctl is-active org.test.ticker").WaitAsync(TimeSpan.FromSeconds(10));
        os.Clock.Advance(3);

        Task<ShellRun> stopping = os.RunAsync("systemctl stop org.test.ticker");
        for (int second = 0; second < 10 && !stopping.IsCompleted; second++)
        {
            await Task.WhenAny(stopping, Task.Delay(50));
            os.Clock.Advance();
        }

        ShellRun stop = await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, start.ExitCode);
        Assert.Equal(0, active.ExitCode);
        Assert.Equal(0, stop.ExitCode);
        Assert.Null(os.FindPid("org.test.ticker"));
        // The dedicated worker is freed with its process; the ready worker is back.
        await OsFixture.WaitUntilAsync(() =>
            os.Pool!.GetWorkers().All(w => w.Role != WorkerRole.Dedicated)
            && os.Pool.GetWorkers().Any(w => w.Role == WorkerRole.Ready));
        string log = await os.ReadAppLogAsync("org.test.ticker");
        Assert.Contains("ticker started in RemoteAppExecutionContext", log, StringComparison.Ordinal);
        Assert.Contains("ticker stopping", log, StringComparison.Ordinal);
    }
    /// <summary>
    /// ADR 0097 through the worker protocol: the service's ready notification crosses svc.notify,
    /// <c>kill -9</c> leaves the unit <c>failed (Result: killed)</c>, <c>is-failed</c> says so, and
    /// <c>reset-failed</c> brings it back to <c>inactive</c>.
    /// </summary>
    [Fact]
    public async Task A_killed_service_reads_as_failed_in_systemctl_until_reset_failed()
    {
        await using OsFixture os = await WorkerPoolTests.StartedAsync();

        await os.RunAsync("systemctl start org.test.ticker").WaitAsync(TimeSpan.FromSeconds(10));
        await OsFixture.WaitUntilAsync(() => os.FindPid("org.test.ticker") is not null);
        long pid = os.FindPid("org.test.ticker")!.Value;
        ShellRun running = await WaitForStatusAsync(os, "active (running)");

        await os.RunAsync(os.OpenShell(), $"kill -9 {pid}").WaitAsync(TimeSpan.FromSeconds(10));
        ShellRun failed = await WaitForStatusAsync(os, "(Result: killed)");
        ShellRun isFailed = await os.RunAsync("systemctl is-failed org.test.ticker").WaitAsync(TimeSpan.FromSeconds(10));
        ShellRun isActive = await os.RunAsync("systemctl is-active org.test.ticker").WaitAsync(TimeSpan.FromSeconds(10));
        ShellRun reset = await os.RunAsync("systemctl reset-failed org.test.ticker").WaitAsync(TimeSpan.FromSeconds(10));
        ShellRun afterReset = await os.RunAsync("systemctl is-failed org.test.ticker").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains($"Main PID: {pid}", running.Stdout, StringComparison.Ordinal);
        Assert.Contains("failed", failed.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, isFailed.ExitCode);
        Assert.Equal("failed", isFailed.Stdout.Trim());
        Assert.Equal(3, isActive.ExitCode);
        Assert.Equal(0, reset.ExitCode);
        Assert.NotEqual(0, afterReset.ExitCode);
        Assert.Equal("inactive", afterReset.Stdout.Trim());
    }

    private static async Task<ShellRun> WaitForStatusAsync(OsFixture os, string expected)
    {
        ShellRun status = new(0, string.Empty, string.Empty);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            status = await os.RunAsync("systemctl status org.test.ticker").WaitAsync(TimeSpan.FromSeconds(10));
            if (status.Stdout.Contains(expected, StringComparison.Ordinal))
            {
                return status;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"systemctl status never showed '{expected}':\n{status.Stdout}");
        return status;
    }
}
