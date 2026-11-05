using Kazo.Os.Platform.WorkerHost.Host;
using Kazo.Os.Simulation.Abstractions.Workers;

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>
/// ADR 0071 ("toujours un worker prêt") and ADR 0070, sections 2 and 3: taking the ready worker,
/// its replacement, overflow while every core is busy, return to size, ceiling, a request before the
/// system is started, dedicated workers and <c>/proc/kazoos/workers</c>.
/// </summary>
public sealed class WorkerPoolTests
{
    [Fact]
    public async Task Before_the_system_is_started_a_request_starts_its_own_worker()
    {
        await using OsFixture os = await OsFixture.CreateAsync(workers: true);

        ShellRun run = await os.RunAsync("echo early").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("early\n", run.Stdout);
        Assert.False(os.Runtime!.IdleWaited);
        WorkerSnapshot worker = Assert.Single(os.Pool!.GetWorkers());
        Assert.Equal(WorkerRole.Pool, worker.Role);
        Assert.Equal(0, worker.Core);
    }

    [Fact]
    public async Task Once_started_the_pool_fills_up_behind_a_ready_worker_after_the_idle_wait()
    {
        await using OsFixture os = await OsFixture.CreateAsync(workers: true, poolOptions: OsFixture.FastOptions with { VirtualCores = 2 });

        os.Startup.MarkStarted();
        await OsFixture.WaitUntilAsync(() => Ready(os) == 1 && Pool(os) == 2 && os.Pool!.GetWorkers().All(w => w.State == WorkerState.Idle));

        Assert.True(os.Runtime!.IdleWaited);
        Assert.Equal(3, os.Runtime.StartedCount);
        // The ready worker is started first, then the cores (ADR 0071, section 2).
        Assert.Equal(WorkerRole.Ready, os.Pool!.GetWorkers()[0].Role);
        Assert.Equal([0, 1], os.Pool.GetWorkers().Where(w => w.Role == WorkerRole.Pool).Select(w => w.Core!.Value).Order());
    }

    [Fact]
    public async Task A_dedicated_process_takes_the_ready_worker_which_is_replaced_at_once_then_freed()
    {
        await using OsFixture os = await StartedAsync();
        int readyId = os.Pool!.GetWorkers().Single(w => w.Role == WorkerRole.Ready).Id;

        ShellRun run = await os.RunAsync("dedicated-echo on my own").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("on my own\n", run.Stdout);
        // Taken, replaced by a new ready worker, and freed when its process ended (ADR 0070, section 3).
        await OsFixture.WaitUntilAsync(() => Ready(os) == 1 && os.Pool.GetWorkers().All(w => w.Id != readyId));
        Assert.DoesNotContain(os.Pool.GetWorkers(), w => w.Role == WorkerRole.Dedicated);
        Assert.Equal(1, Pool(os));
    }

    [Fact]
    public async Task When_every_core_is_busy_the_ready_worker_overflows_then_the_pool_returns_to_its_size()
    {
        await using OsFixture os = await StartedAsync();
        TestCommands.ArmSpinners();
        Task<ShellRun> spinning = os.RunAsync("spin");
        await OsFixture.WaitUntilAsync(() => os.Pool!.GetWorkers().Any(w => w.Role == WorkerRole.Pool && w.ProcessIds.Count == 1));
        await Task.Delay(OsFixture.FastOptions.BusyThreshold * 2); // the core's heartbeat goes stale

        ShellRun echo = await os.RunAsync(os.OpenShell(), "echo overflow").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("overflow\n", echo.Stdout);
        Assert.Contains(os.Pool!.GetWorkers(), w => w.Role == WorkerRole.Overflow);
        await OsFixture.WaitUntilAsync(() => Ready(os) == 1, because: "the taken ready worker is replaced");
        // Empty for OverflowIdleTimeout: the overflow core is removed.
        await OsFixture.WaitUntilAsync(() => os.Pool.GetWorkers().All(w => w.Role != WorkerRole.Overflow), TimeSpan.FromSeconds(5));

        TestCommands.ReleaseSpinners();
        Assert.Equal(0, (await spinning.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode);
    }

    [Fact]
    public async Task A_command_that_spins_at_once_does_not_hold_the_next_command_behind_it()
    {
        await using OsFixture os = await StartedAsync();
        TestCommands.ArmSpinners();
        Task<ShellRun> spinning = os.RunAsync("spin");
        await OsFixture.WaitUntilAsync(() => os.Pool!.GetWorkers().Any(w => w.Role == WorkerRole.Pool && w.ProcessIds.Count == 1));

        // No wait for the core's heartbeat to go stale: it never proved the new process yields (ADR 0072).
        ShellRun echo = await os.RunAsync(os.OpenShell(), "echo right away").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("right away\n", echo.Stdout);
        Assert.Contains(os.Pool!.GetWorkers(), w => w.Role == WorkerRole.Overflow);
        TestCommands.ReleaseSpinners();
        Assert.Equal(0, (await spinning.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode);
    }

    [Fact]
    public async Task At_the_ceiling_a_request_waits_for_a_free_worker_and_syslog_notes_it_once()
    {
        // hardwareConcurrency 2: one core and the ready worker fill the machine.
        await using OsFixture os = await StartedAsync(hardwareConcurrency: 2);
        Assert.Equal(2, os.Pool!.Capacity);
        Assert.Equal(1, os.Pool.PoolSize);
        TestCommands.ArmSpinners();
        Task<ShellRun> first = os.RunAsync("spin");
        await OsFixture.WaitUntilAsync(() => os.Pool.GetWorkers().Any(w => w.ProcessIds.Count == 1));
        await Task.Delay(OsFixture.FastOptions.BusyThreshold * 2);
        Task<ShellRun> second = os.RunAsync(os.OpenShell(), "spin");
        await OsFixture.WaitUntilAsync(() => os.Pool.GetWorkers().Count(w => w.ProcessIds.Count == 1) == 2, because: "the ready worker overflows");
        await Task.Delay(OsFixture.FastOptions.BusyThreshold * 2);

        Task<ShellRun> waiting = os.RunAsync(os.OpenShell(), "echo patient");
        await Task.Delay(300);

        Assert.False(waiting.IsCompleted);
        Assert.Equal(2, os.Runtime!.AliveCount);
        Assert.Single(os.Syslog, line => line.Contains("worker pool saturated", StringComparison.Ordinal));

        TestCommands.ReleaseSpinners();
        Assert.Equal("patient\n", (await waiting.WaitAsync(TimeSpan.FromSeconds(10))).Stdout);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Every_stage_of_a_pipeline_runs_on_the_same_worker()
    {
        await using OsFixture os = await StartedAsync(VirtualCores: 2);

        ShellRun run = await os.RunAsync("seq 50 | grep 5 | wc -l").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("6", run.Stdout.Trim());
        // Stages are placed together: one core ran all three, the other none (ADR 0070, section 2).
        Assert.Equal([0, 3], os.Pool!.Connections.Where(w => w.Role == WorkerRole.Pool).Select(w => w.HostedTotal).Order());
    }

    [Fact]
    public async Task Proc_kazoos_workers_lists_every_worker_with_its_role_and_processes()
    {
        await using OsFixture os = await StartedAsync();

        ShellRun run = await os.RunAsync("cat /proc/kazoos/workers").WaitAsync(TimeSpan.FromSeconds(10));

        string[] lines = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // ADR 0131, section 5: the file starts with the cross-origin isolation of the page (none here).
        Assert.Equal("cross-origin-isolated: no", lines[0]);
        Assert.Equal("isolation-reason: not-browser", lines[1]);
        int capacity = Array.IndexOf(lines, "capacity 4");
        Assert.True(capacity > 0, run.Stdout);
        Assert.Equal("pool_size 1", lines[capacity + 1]);
        Assert.StartsWith("worker role", lines[capacity + 2], StringComparison.Ordinal);
        long catPid = os.Manager.GetHistory().Last(p => p.AppId == "org.kazoos.cmd.cat").Pid.Value;
        Assert.Contains(lines, line => line.Contains(" pool ", StringComparison.Ordinal) && line.Contains("running", StringComparison.Ordinal) && line.EndsWith($" {catPid}", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains(" ready ", StringComparison.Ordinal) && line.Contains("idle", StringComparison.Ordinal));
        Assert.Contains("kazoos", (await os.RunAsync("ls /proc")).Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_worker_pool_proc_has_no_kazoos_directory()
    {
        await using OsFixture os = await OsFixture.CreateAsync(workers: false);

        ShellRun run = await os.RunAsync("cat /proc/kazoos/workers");

        Assert.NotEqual(0, run.ExitCode);
        Assert.DoesNotContain("kazoos", (await os.RunAsync("ls /proc")).Stdout, StringComparison.Ordinal);
    }

    private static int Ready(OsFixture os) => os.Pool!.GetWorkers().Count(w => w.Role == WorkerRole.Ready && w.State == WorkerState.Idle);

    private static int Pool(OsFixture os) => os.Pool!.GetWorkers().Count(w => w.Role == WorkerRole.Pool && w.State != WorkerState.Starting);

    /// <summary>An OS whose system is started, with its pool and its ready worker warmed up.</summary>
    internal static async Task<OsFixture> StartedAsync(int hardwareConcurrency = 4, int VirtualCores = 1, WorkerPoolOptions? options = null)
    {
        OsFixture os = await OsFixture.CreateAsync(
            workers: true,
            poolOptions: (options ?? OsFixture.FastOptions) with { VirtualCores = VirtualCores },
            hardwareConcurrency: hardwareConcurrency);
        os.Startup.MarkStarted();
        await OsFixture.WaitUntilAsync(
            () => Ready(os) == 1 && Pool(os) == os.Pool!.PoolSize && os.Pool.GetWorkers().All(w => w.State == WorkerState.Idle),
            because: "the pool warms up");
        return os;
    }
}
