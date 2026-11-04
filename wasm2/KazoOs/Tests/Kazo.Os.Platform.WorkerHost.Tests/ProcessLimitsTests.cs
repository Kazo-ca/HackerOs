using Kazo.Os.Platform.Core.Processes.Accounting;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>
/// ADR 0125 end to end, through the real shell, commands and gateways, in-process and in a Web Worker:
/// <c>ulimit</c>, <c>prlimit</c>, <c>/proc/&lt;pid&gt;/limits</c>, <c>EMFILE</c>, <c>EAGAIN</c> and the
/// CPU limit (<c>SIGXCPU</c>, exit 152; <c>SIGKILL</c>, exit 137).
/// </summary>
public sealed class ProcessLimitsTests
{
    public static TheoryData<bool> Hosts => new() { false, true };

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Ulimit_prints_the_kernel_defaults(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);

        Assert.Equal("1024\n", (await os.RunAsync("ulimit -n")).Stdout);
        Assert.Equal("524288\n", (await os.RunAsync("ulimit -Hn")).Stdout);
        Assert.Equal("4096\n", (await os.RunAsync("ulimit -u")).Stdout);
        Assert.Equal("unlimited\n", (await os.RunAsync("ulimit -t")).Stdout);
        Assert.Equal("unlimited\n", (await os.RunAsync("ulimit")).Stdout);
        Assert.Equal(
            "file size                   (blocks, -f) unlimited\n" +
            "max memory size             (kbytes, -m) unlimited\n" +
            "open files                          (-n) 1024\n" +
            "cpu time                   (seconds, -t) unlimited\n" +
            "max user processes                  (-u) 4096\n",
            (await os.RunAsync("ulimit -a")).Stdout);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Ulimit_refuses_what_setrlimit_refuses(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);

        ShellRun fileSize = await os.RunAsync("ulimit 100");
        Assert.Equal(1, fileSize.ExitCode);
        Assert.Equal("hsh: ulimit: file size: cannot modify limit: Operation not permitted\n", fileSize.Stderr);

        ShellRun raise = await os.RunAsync("ulimit -Hn 600000");
        Assert.Equal(1, raise.ExitCode);
        Assert.Equal("hsh: ulimit: open files: cannot modify limit: Operation not permitted\n", raise.Stderr);

        ShellRun number = await os.RunAsync("ulimit -n many");
        Assert.Equal(1, number.ExitCode);
        Assert.Equal("hsh: ulimit: many: invalid number\n", number.Stderr);

        Assert.Equal(2, (await os.RunAsync("ulimit -z")).ExitCode);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task A_lowered_limit_is_inherited_by_the_commands_the_shell_starts(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);

        Assert.Equal(0, (await os.RunAsync("ulimit -S -n 512")).ExitCode);
        Assert.Equal("512\n", (await os.RunAsync("ulimit -n")).Stdout);
        Assert.Equal("524288\n", (await os.RunAsync("ulimit -H -n")).Stdout);

        // The soft value may go back up to the hard one, without privilege.
        ShellRun raised = await os.RunAsync("ulimit -S -n 2048");
        Assert.True(raised.ExitCode == 0, raised.Stderr);
        ShellRun prlimit = await os.RunAsync("prlimit --nofile --noheadings");
        Assert.Equal(0, prlimit.ExitCode);
        Assert.Equal("NOFILE max number of open files 2048 524288 files\n", prlimit.Stdout);

        ShellRun limits = await os.RunAsync("cat /proc/self/limits");
        Assert.Contains("Max open files            2048                 524288               files     \n", limits.Stdout, StringComparison.Ordinal);
        Assert.Contains(workers ? "# enforcement: strict (worker)\n" : "# enforcement: best-effort (main thread)\n", limits.Stdout, StringComparison.Ordinal);
        Assert.Contains("# cpu, rss: inactive (not measured)\n", limits.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Prlimit_changes_another_process_of_the_same_user(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        long shell = os.Host.ProcessId.Value;

        ShellRun set = await os.RunAsync($"prlimit --pid {shell} --nofile=100:200 --nproc=50");
        Assert.True(set.ExitCode == 0, set.Stderr);
        Assert.Equal("100\n", (await os.RunAsync("ulimit -n")).Stdout);
        Assert.Equal("200\n", (await os.RunAsync("ulimit -Hn")).Stdout);
        Assert.Equal("50\n", (await os.RunAsync("ulimit -Hu")).Stdout);

        ShellRun raise = await os.RunAsync($"prlimit --pid {shell} --nofile=:300");
        Assert.Equal(1, raise.ExitCode);
        Assert.Equal("prlimit: failed to set the NOFILE resource limit: Operation not permitted\n", raise.Stderr);

        ShellRun missing = await os.RunAsync("prlimit --pid 99999");
        Assert.Equal(1, missing.ExitCode);
        Assert.Equal("prlimit: failed to get the process resource limit: No such process\n", missing.Stderr);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Opening_a_file_at_the_nofile_limit_fails_with_emfile(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        await os.WriteFileAsync("/tmp/limits.txt", "content\n");

        Assert.Equal("content\n", (await os.RunAsync("cat /tmp/limits.txt")).Stdout);
        // 0, 1 and 2 are the standard streams: with a limit of 3 no file can be opened.
        Assert.Equal(0, (await os.RunAsync("ulimit -n 3")).ExitCode);
        ShellRun cat = await os.RunAsync("cat /tmp/limits.txt");

        Assert.Equal(1, cat.ExitCode);
        Assert.Contains("Too many open files", cat.Stderr, StringComparison.Ordinal);
        Assert.Equal(string.Empty, cat.Stdout);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task Starting_a_process_at_the_nproc_limit_fails_with_eagain(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        // The terminal itself is the user's one process.
        Assert.Equal(0, (await os.RunAsync("ulimit -u 1")).ExitCode);

        ShellRun echo = await os.RunAsync("whoami");

        Assert.Equal(1, echo.ExitCode);
        Assert.Equal("hsh: fork: Resource temporarily unavailable\n", echo.Stderr);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task The_soft_cpu_limit_sends_sigxcpu_and_the_command_exits_152(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        using ResourceLimitEnforcer enforcer = Enforcer(os);
        Assert.Equal(0, (await os.RunAsync("ulimit -S -t 1")).ExitCode);

        ShellRun stress = await RunCheckingAsync(os, enforcer, "stress --yield");

        Assert.Equal(152, stress.ExitCode);
        Assert.EndsWith("CPU time limit exceeded\n", stress.Stderr, StringComparison.Ordinal);
        Assert.Equal(ProcessExitReason.CpuLimitExceeded, os.Manager.GetHistory().Last(p => p.AppId == StressAppId).ExitReason);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task A_limit_set_in_a_child_shell_stays_in_it(bool workers)
    {
        // The path of build/verify-hosts.sh: the terminal's own shell keeps its limits.
        await using OsFixture os = await StartAsync(workers);
        using ResourceLimitEnforcer enforcer = Enforcer(os);

        ShellRun run = await RunCheckingAsync(os, enforcer, "sh -c 'ulimit -S -t 1; stress --yield'; echo \"exit=$?\"");

        Assert.Equal("exit=152\n", run.Stdout[run.Stdout.IndexOf("exit=", StringComparison.Ordinal)..]);
        Assert.Contains("CPU time limit exceeded", run.Stderr, StringComparison.Ordinal);
        Assert.Equal("unlimited\n", (await os.RunAsync("ulimit -t")).Stdout);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task The_hard_cpu_limit_kills_the_command(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        using ResourceLimitEnforcer enforcer = Enforcer(os);
        // soft = hard: Linux checks the hard limit first, so the process gets SIGKILL.
        Assert.Equal(0, (await os.RunAsync("ulimit -t 1")).ExitCode);

        ShellRun stress = await RunCheckingAsync(os, enforcer, "stress --yield");

        Assert.Equal(137, stress.ExitCode);
        Assert.EndsWith("Killed\n", stress.Stderr, StringComparison.Ordinal);
        Assert.Equal(ProcessExitReason.CpuLimitExceeded, os.Manager.GetHistory().Last(p => p.AppId == StressAppId).ExitReason);
    }

    private const string StressAppId = "org.kazoos.commands.stress";

    private static async Task<OsFixture> StartAsync(bool workers) =>
        workers ? await WorkerPoolTests.StartedAsync() : await OsFixture.CreateAsync(workers: false);

    /// <summary>An enforcer whose CPU measurement says <c>stress</c> already used 2 s of CPU.</summary>
    private static ResourceLimitEnforcer Enforcer(OsFixture os) => new(
        os.Clock, () => os.Manager, os.Manager.ResourceLimits, new StressCpu(os), NullMemoryAccountant.Instance);

    /// <summary>Runs a line and checks the limits until it ends, like the kernel's tick.</summary>
    private static async Task<ShellRun> RunCheckingAsync(OsFixture os, ResourceLimitEnforcer enforcer, string line)
    {
        Task<ShellRun> run = os.RunAsync(line);
        await OsFixture.WaitUntilAsync(() => os.FindPid(StressAppId) is not null || run.IsCompleted, because: "stress starts");
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!run.IsCompleted && DateTime.UtcNow < deadline)
        {
            enforcer.CheckNow();
            await Task.WhenAny(run, Task.Delay(20));
        }

        return await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class StressCpu(OsFixture os) : ICpuTimeAccountant
    {
        public bool IsMeasuring => true;

        public bool TryGetCpuUsage(ProcessId pid, out double coreEquivalents)
        {
            coreEquivalents = 1;
            return true;
        }

        public bool TryGetProcessTimes(ProcessId pid, out ProcessCpuTimes times)
        {
            bool stress = os.Manager.TryGetActive(pid, out ProcessRecord process) && process.AppId == StressAppId;
            times = new ProcessCpuTimes(stress ? TimeSpan.FromSeconds(2) : TimeSpan.Zero, TimeSpan.Zero, 0, TimeSpan.Zero, true);
            return true;
        }

        public bool TryGetThreadTimes(out MeasuredThreadCpuTimes times)
        {
            times = default;
            return false;
        }
    }
}
