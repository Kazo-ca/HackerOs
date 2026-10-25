using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Workers;

namespace HackerOs.Platform.WorkerHost.Tests;

/// <summary>
/// ADR 0070, section 5 and ADR 0069, section 3: a command that never yields no longer freezes the
/// OS; its worker misses its heartbeat ("Not responding"), and stopping it terminates the worker
/// (step 4) — the neighbors end as killed with a syslog line, and the ready worker takes the core.
/// A process that still yields is killed without touching its worker (step 3).
/// </summary>
public sealed class EscalationTests
{
    [Fact]
    public async Task Kill_minus_9_of_a_spinning_command_terminates_its_worker_kills_its_neighbors_and_replaces_the_core()
    {
        await using OsFixture os = await WorkerPoolTests.StartedAsync();
        TestCommands.ArmSpinners();
        WorkerSnapshot core = os.Pool!.GetWorkers().Single(w => w.Role == WorkerRole.Pool);
        Task<ShellRun> sleeping = os.RunAsync(os.OpenShell(), "sleep 100");
        await OsFixture.WaitUntilAsync(
            () => os.FindPid("org.hackeros.commands.sleep") is not null && os.Pool.Connections.Any(w => w.Id == core.Id && w.ProcessCount == 1 && w.IsFree),
            because: "sleep yields: its core heartbeats after taking it");
        Task<ShellRun> spinning = os.RunAsync("spin");
        await OsFixture.WaitUntilAsync(() => os.Pool.GetWorkers().Any(w => w.Id == core.Id && w.ProcessIds.Count == 2), because: "spin joins sleep on the free core");
        await TestCommands.Spinning.WaitAsync(TimeSpan.FromSeconds(10)); // placed is not running yet
        long spinPid = os.FindPid("org.test.spin")!.Value;
        long sleepPid = os.FindPid("org.hackeros.commands.sleep")!.Value;

        // kill is a shell builtin on the main thread: it answers while the worker is stuck.
        ShellRun kill = await os.RunAsync(os.OpenShell(), $"kill -9 {spinPid}").WaitAsync(TimeSpan.FromSeconds(10));
        ShellRun spin = await spinning.WaitAsync(TimeSpan.FromSeconds(10));
        ShellRun sleep = await sleeping.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, kill.ExitCode);
        Assert.Equal(137, spin.ExitCode);
        Assert.Equal("spinning\n", spin.Stdout);
        Assert.Equal("Killed\n", spin.Stderr);
        Assert.Equal(137, sleep.ExitCode);
        Assert.Equal("Killed\n", sleep.Stderr);
        Assert.Equal(ProcessExitReason.Killed, os.Manager.GetHistory().Last(p => p.Pid.Value == sleepPid).ExitReason);
        Assert.Contains($"[{sleepPid}] killed: worker {core.Id} terminated to stop pid {spinPid}", os.Syslog);
        // The ready worker took core 0 at once; a new ready worker is started behind it.
        await OsFixture.WaitUntilAsync(() =>
            os.Pool.GetWorkers().Any(w => w.Role == WorkerRole.Pool && w.Core == 0 && w.Id != core.Id)
            && os.Pool.GetWorkers().Any(w => w.Role == WorkerRole.Ready && w.State == WorkerState.Idle)
            && os.Pool.GetWorkers().All(w => w.Id != core.Id));
        Assert.Equal("still here\n", (await os.RunAsync("echo still here").WaitAsync(TimeSpan.FromSeconds(10))).Stdout);
    }

    [Fact]
    public async Task A_spinning_command_shows_not_responding_while_it_blocks()
    {
        await using OsFixture os = await WorkerPoolTests.StartedAsync();
        TestCommands.ArmSpinners();
        Task<ShellRun> spinning = os.RunAsync("spin");

        await OsFixture.WaitUntilAsync(
            () => os.Pool!.GetWorkers().Any(w => w.State == WorkerState.NotResponding),
            because: "the heartbeat stops for longer than the unresponsive threshold");
        ShellRun workers = await os.RunAsync(os.OpenShell(), "cat /proc/hackeros/workers").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("not-responding", workers.Stdout, StringComparison.Ordinal);
        await OsFixture.WaitUntilAsync(() => os.Syslog.Any(line => line.Contains("not responding", StringComparison.Ordinal)));

        TestCommands.ReleaseSpinners();
        Assert.Equal(0, (await spinning.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode);
        await OsFixture.WaitUntilAsync(() => os.Pool!.GetWorkers().All(w => w.State != WorkerState.NotResponding), because: "it answers again");
    }

    [Fact]
    public async Task Sigterm_to_a_spinning_command_escalates_to_the_worker_after_the_grace_period()
    {
        await using OsFixture os = await WorkerPoolTests.StartedAsync();
        TestCommands.ArmSpinners();
        Task<ShellRun> spinning = os.RunAsync("spin");
        await TestCommands.Spinning.WaitAsync(TimeSpan.FromSeconds(10)); // SIGTERM must find it stuck
        long spinPid = os.FindPid("org.test.spin")!.Value;

        Task<ShellRun> kill = os.RunAsync(os.OpenShell(), $"kill {spinPid}");
        await OsFixture.WaitUntilAsync(() => os.Manager.TryGetActive(ProcessId.FromInt64(spinPid), out ProcessRecord p) && p.State == ProcessState.Stopping);
        Assert.False(spinning.IsCompleted); // SIGTERM is never delivered: the worker's thread is stuck.
        for (int second = 0; second < 6 && !spinning.IsCompleted; second++)
        {
            os.Clock.Advance(); // the 5 s grace period of kill (ADR 0069)
            await Task.Delay(50);
        }

        ShellRun spin = await spinning.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(137, spin.ExitCode);
        Assert.Equal(0, (await kill.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode);
        Assert.Equal(ProcessExitReason.Timeout, os.Manager.GetHistory().Last(p => p.Pid.Value == spinPid).ExitReason);
        await OsFixture.WaitUntilAsync(() => os.Syslog.Any(line => line.Contains($"terminated: pid {spinPid} did not stop", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_command_that_still_yields_is_killed_without_terminating_its_worker()
    {
        await using OsFixture os = await WorkerPoolTests.StartedAsync();
        TestCommands.ArmSpinners();
        int coreId = os.Pool!.GetWorkers().Single(w => w.Role == WorkerRole.Pool).Id;
        Task<ShellRun> stubborn = os.RunAsync("stubborn");
        await OsFixture.WaitUntilAsync(() => os.FindPid("org.test.stubborn") is not null);
        long pid = os.FindPid("org.test.stubborn")!.Value;

        await os.RunAsync(os.OpenShell(), $"kill -9 {pid}").WaitAsync(TimeSpan.FromSeconds(10));
        ShellRun run = await stubborn.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(OsFixture.FastOptions.KillAcknowledgeTimeout * 2);

        Assert.Equal(137, run.ExitCode);
        // Step 3: the worker confirmed it dropped the process; it lives on.
        Assert.Contains(os.Pool.GetWorkers(), w => w.Id == coreId && w.ProcessIds.Count == 0);
        Assert.DoesNotContain(os.Syslog, line => line.Contains("terminated", StringComparison.Ordinal));
        TestCommands.ReleaseSpinners();
    }

    [Fact]
    public async Task Ctrl_c_interrupts_a_command_in_a_worker()
    {
        await using OsFixture os = await WorkerPoolTests.StartedAsync();
        using CancellationTokenSource ctrlC = new();
        Task<ShellRun> sleeping = os.RunAsync("sleep 100", cancellationToken: ctrlC.Token);
        await OsFixture.WaitUntilAsync(() => os.FindPid("org.hackeros.commands.sleep") is not null);

        await ctrlC.CancelAsync();

        Assert.Equal(130, (await sleeping.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode);
    }

    [Fact]
    public async Task A_fault_in_a_worker_is_reported_like_a_local_fault()
    {
        await using OsFixture inProcess = await OsFixture.CreateAsync(workers: false);
        await using OsFixture inWorker = await WorkerPoolTests.StartedAsync();

        ShellRun local = await inProcess.RunAsync("throw");
        ShellRun remote = await inWorker.RunAsync("throw").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(local, remote);
        Assert.Contains("boom", remote.Stderr, StringComparison.Ordinal);
    }
}
