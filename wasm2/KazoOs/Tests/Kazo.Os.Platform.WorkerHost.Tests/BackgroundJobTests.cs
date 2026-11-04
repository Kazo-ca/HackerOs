using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.Threading;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.WorkerHost.Tests;
using Kazo.Os.Simulation.Abstractions.Processes;

// ADR 0128, section 2: the inputs and results of the test tasks cross as JSON.
[assembly: HackWireContext(typeof(BackgroundJobsJsonContext))]

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>The input of the <c>Sum</c> task.</summary>
public sealed record SumInput(int Count, int Delay);

/// <summary>The result of the <c>Sum</c> task: the total and where it ran.</summary>
public sealed record SumResult(long Total, string Where, long Pid, long ParentPid);

/// <summary>An input no context declares.</summary>
public sealed record UndeclaredJobInput(int N);

/// <summary>Serializer context of the test tasks (ADR 0128, section 2).</summary>
[JsonSerializable(typeof(SumInput))]
[JsonSerializable(typeof(SumResult))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
internal sealed partial class BackgroundJobsJsonContext : JsonSerializerContext;

/// <summary>
/// ADR 0128: <c>HackBackground.RunAsync</c> runs a static method of the app as a child process, on
/// the main thread without workers and in a worker with them, with the same result, reports,
/// cancellation, faults and kill. Each scenario runs in both modes.
/// </summary>
public sealed class BackgroundJobTests
{
    public static TheoryData<bool> BothModes => new() { false, true };

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task A_task_returns_its_result_as_a_child_process_and_reports_progress(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);

        ShellRun run = await os.RunAsync("bg-sum 20").WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(run.ExitCode == 0, run.Stderr);
        string[] lines = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("total=210", lines[0]);
        Assert.Equal(workers ? "where=worker" : "where=main", lines[1]);
        Assert.Equal("child=yes", lines[2]);
        Assert.Equal("progress=yes", lines[3]);
        Assert.Equal("last=1.00 done", lines[4]);
        // The task's process is gone and ended with code 0, under the command's name.
        ProcessRecord job = await LastJobAsync(os, "org.test.bg-sum");
        Assert.Equal(0, job.ExitCode);
        Assert.Empty(os.BackgroundJobs.GetJobs());
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task A_task_class_runs_like_a_static_method(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);

        ShellRun run = await os.RunAsync("bg-class hello").WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("5\n", run.Stdout);
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task An_exception_of_the_task_comes_back_as_a_background_job_exception(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);

        ShellRun run = await os.RunAsync("bg-fault").WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("System.InvalidOperationException: kaboom trace=yes\n", run.Stdout);
        Assert.Equal(1, (await LastJobAsync(os, "org.test.bg-fault")).ExitCode);
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task Cancelling_the_token_ends_the_task_with_code_130(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);

        ShellRun run = await os.RunAsync("bg-cancel").WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("cancelled\n", run.Stdout);
        Assert.Equal(130, (await LastJobAsync(os, "org.test.bg-cancel")).ExitCode);
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task Kill_minus_9_of_a_task_ends_it_without_closing_the_app(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        BackgroundJobCommands.Reset();

        Task<ShellRun> running = os.RunAsync("bg-hold 1");
        await OsFixture.WaitUntilAsync(() => os.BackgroundJobs.GetJobs().Count == 1 && BackgroundJobCommands.Started >= 1, TimeSpan.FromSeconds(20), "the task starts");
        BackgroundJobInfo job = os.BackgroundJobs.GetJobs().Single();
        long appPid = job.ParentPid.Value;
        Assert.True(os.Manager.TryGetActive(job.ParentPid, out ProcessRecord? app));
        Assert.Equal("org.test.bg-hold", app!.AppId);
        Assert.Equal("org.test.bg-hold", os.Manager.GetActiveProcesses().Single(p => p.Pid == job.Pid).AppId);
        Assert.Equal(workers ? BackgroundJobHost.Worker : BackgroundJobHost.MainThread, job.Host);

        // What ps and /proc show of it.
        string status = await os.ReadFileAsync($"/proc/{job.Pid.Value}/status");
        Assert.Contains("Name:\tbg-hold:Hold\n", status, StringComparison.Ordinal);
        Assert.Contains("Job:\tbg-hold:Hold\n", status, StringComparison.Ordinal);
        Assert.Contains($"Host:\t{(workers ? "Worker" : "MainThread")}\n", status, StringComparison.Ordinal);
        Assert.Contains($"PPid:\t{appPid}\n", status, StringComparison.Ordinal);
        ShellRun ps = await os.RunAsync(os.OpenShell(), "ps").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("bg-hold:Hold", ps.Stdout, StringComparison.Ordinal);

        // ADR 0127: the task's nice option is its process's niceness, in ps and in the pool queue.
        Assert.Equal(5, job.Nice);
        Assert.Equal(5, os.Manager.GetNice(job.Pid));
        ShellRun niced = await os.RunAsync(os.OpenShell(), "ps -o ni,cmd").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("  5 bg-hold:Hold", niced.Stdout, StringComparison.Ordinal);

        ShellRun kill = await os.RunAsync(os.OpenShell(), $"kill -9 {job.Pid.Value}").WaitAsync(TimeSpan.FromSeconds(10));
        ShellRun run = await running.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, kill.ExitCode);
        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("killed Killed\n", run.Stdout);
        Assert.Equal(ProcessExitReason.Killed, os.Manager.GetHistory().Last(record => record.Pid == job.Pid).ExitReason);
        BackgroundJobCommands.Release();
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task An_app_kills_its_own_task_without_the_process_management_capability(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        BackgroundJobCommands.Reset();
        Assert.DoesNotContain(AppCapabilities.ProcessManage, os.Catalog.Manifests["org.test.bg-selfkill"].Capabilities);

        ShellRun run = await os.RunAsync("bg-selfkill").WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("killed Killed\n", run.Stdout);
        BackgroundJobCommands.Release();
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task Kill_minus_9_of_the_app_kills_its_tasks_at_once(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        BackgroundJobCommands.Reset();

        Task<ShellRun> running = os.RunAsync("bg-hold 1 nocatch");
        await OsFixture.WaitUntilAsync(() => os.BackgroundJobs.GetJobs().Count == 1 && BackgroundJobCommands.Started >= 1, TimeSpan.FromSeconds(20), "the task starts");
        BackgroundJobInfo job = os.BackgroundJobs.GetJobs().Single();

        ShellRun kill = await os.RunAsync(os.OpenShell(), $"kill -9 {job.ParentPid.Value}").WaitAsync(TimeSpan.FromSeconds(10));
        await running.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, kill.ExitCode);
        await OsFixture.WaitUntilAsync(() => !os.Manager.TryGetActive(job.Pid, out _), TimeSpan.FromSeconds(10), "the task dies with its app");
        await OsFixture.WaitUntilAsync(() => os.BackgroundJobs.GetJobs().Count == 0, TimeSpan.FromSeconds(10), "the task is forgotten");
        BackgroundJobCommands.Release();
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task Closing_the_app_asks_its_tasks_to_stop_and_a_task_that_honors_it_ends_with_code_130(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        BackgroundJobCommands.Reset();

        Task<ShellRun> running = os.RunAsync("bg-forever");
        await OsFixture.WaitUntilAsync(() => os.BackgroundJobs.GetJobs().Count == 1, TimeSpan.FromSeconds(20), "the task starts");
        BackgroundJobInfo job = os.BackgroundJobs.GetJobs().Single();

        // ADR 0147: the app ends without a SIGKILL (its window closed); the task gets a SIGTERM, not a kill.
        os.Manager.Kill(job.ParentPid, ProcessExitReason.CloseRequested);

        await OsFixture.WaitUntilAsync(() => !os.Manager.TryGetActive(job.Pid, out _), TimeSpan.FromSeconds(20), "the task ends on its stop request");
        ProcessRecord ended = os.Manager.GetHistory().Last(record => record.Pid == job.Pid);
        Assert.Equal(130, ended.ExitCode);
        Assert.NotEqual(ProcessExitReason.Timeout, ended.ExitReason);
        await OsFixture.WaitUntilAsync(() => os.BackgroundJobs.GetJobs().Count == 0, TimeSpan.FromSeconds(10), "the task is forgotten");
        await running.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task A_task_that_ignores_the_stop_request_is_killed_five_seconds_after_its_app_closed(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        BackgroundJobCommands.Reset();

        Task<ShellRun> running = os.RunAsync("bg-hold 1 nocatch");
        await OsFixture.WaitUntilAsync(() => os.BackgroundJobs.GetJobs().Count == 1 && BackgroundJobCommands.Started >= 1, TimeSpan.FromSeconds(20), "the task starts");
        BackgroundJobInfo job = os.BackgroundJobs.GetJobs().Single();

        os.Manager.Kill(job.ParentPid, ProcessExitReason.CloseRequested);

        // Still there, stopping, with its app gone: the grace period runs on the simulation clock.
        Assert.False(os.Manager.TryGetActive(job.ParentPid, out _));
        Assert.True(os.Manager.TryGetActive(job.Pid, out ProcessRecord? stopping));
        Assert.Equal(ProcessState.Stopping, stopping!.State);
        os.Clock.Advance(4);
        await Task.Delay(100);
        Assert.True(os.Manager.TryGetActive(job.Pid, out _));

        os.Clock.Advance(1);
        await OsFixture.WaitUntilAsync(() => !os.Manager.TryGetActive(job.Pid, out _), TimeSpan.FromSeconds(10), "the task is killed at the end of the grace period");
        Assert.Equal(ProcessExitReason.Timeout, os.Manager.GetHistory().Last(record => record.Pid == job.Pid).ExitReason);
        await OsFixture.WaitUntilAsync(() => os.BackgroundJobs.GetJobs().Count == 0, TimeSpan.FromSeconds(10), "the task is forgotten");
        BackgroundJobCommands.Release();
        await running.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task At_most_the_limit_of_tasks_of_one_app_run_at_once(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        BackgroundJobCommands.Reset();

        Task<ShellRun> running = os.RunAsync("bg-hold 3");
        await OsFixture.WaitUntilAsync(() => BackgroundJobCommands.Started >= 2, TimeSpan.FromSeconds(20), "two tasks start");
        await Task.Delay(200);

        Assert.Equal(2, os.BackgroundJobs.MaxConcurrentJobsPerApp);
        Assert.Equal(2, BackgroundJobCommands.Started);
        Assert.Equal(2, os.BackgroundJobs.GetJobs().Count);

        BackgroundJobCommands.Release();
        ShellRun run = await running.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("held 3\n", run.Stdout);
        Assert.Equal(3, BackgroundJobCommands.Started);
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public async Task An_undeclared_input_is_refused_before_any_process_starts(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        int before = os.Manager.GetHistory().Count;

        ShellRun run = await os.RunAsync("bg-undeclared").WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("refused: names JsonSerializable\n", run.Stdout);
        // Only the command itself ran: no task process was created.
        Assert.Single(os.Manager.GetHistory().Skip(before), record => record.AppId == "org.test.bg-undeclared");
    }

    [Fact]
    public async Task A_task_outside_the_apps_assembly_is_refused_by_the_os()
    {
        await using OsFixture os = await StartAsync(workers: false);

        ShellRun run = await os.RunAsync("bg-foreign").WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.Equal("refused: names ADR 0128\n", run.Stdout);
    }

    /// <summary>The last ended task process of <paramref name="appId"/>: a process of the app whose parent is the app too.</summary>
    /// <remarks>A worker's caller can see its cancellation before the OS ended the task's process: wait for it.</remarks>
    private static async Task<ProcessRecord> LastJobAsync(OsFixture os, string appId)
    {
        ProcessRecord? found = null;
        await OsFixture.WaitUntilAsync(() => (found = Find()) is not null, because: $"a task of {appId} ends");
        return found!;

        ProcessRecord? Find()
        {
            IReadOnlyList<ProcessRecord> history = os.Manager.GetHistory();
            HashSet<ProcessId> apps = [.. history.Where(record => record.AppId == appId).Select(static record => record.Pid)];
            return history.LastOrDefault(record => record.AppId == appId && record.ParentPid is { } parent && apps.Contains(parent));
        }
    }

    private static async Task<OsFixture> StartAsync(bool workers)
    {
        OsFixture os = await OsFixture.CreateAsync(workers);
        if (workers)
        {
            await os.Pool!.StartAsync();
        }

        return os;
    }
}

/// <summary>The test apps of <see cref="BackgroundJobTests"/> and their tasks.</summary>
internal static class BackgroundJobCommands
{
    private static int s_started;
    private static volatile bool s_release;
    private static long s_lastPid;

    public static IReadOnlyList<AppManifest> Manifests { get; } =
    [
        TestCommands.Command<SumCommand>("bg-sum"),
        TestCommands.Command<ClassCommand>("bg-class"),
        TestCommands.Command<FaultCommand>("bg-fault"),
        TestCommands.Command<CancelCommand>("bg-cancel"),
        TestCommands.Command<HoldCommand>("bg-hold"),
        TestCommands.Command<ForeverCommand>("bg-forever"),
        TestCommands.Command<UndeclaredCommand>("bg-undeclared"),
        TestCommands.Command<ForeignCommand>("bg-foreign"),
        TestCommands.Command<SelfKillCommand>("bg-selfkill")
    ];

    /// <summary>Gets how many <c>Hold</c> tasks started since <see cref="Reset"/>.</summary>
    public static int Started => Volatile.Read(ref s_started);

    public static void Reset()
    {
        Volatile.Write(ref s_started, 0);
        s_release = false;
    }

    public static void Release() => s_release = true;

    // ---- the tasks: static methods of the app's assembly -------------------------------------

    /// <summary>Adds 1..Count, one report per step.</summary>
    public static async Task<SumResult> Sum(SumInput input, HackBackgroundContext context)
    {
        long total = 0;
        for (int i = 1; i <= input.Count; i++)
        {
            total += i;
            context.Report((double)i / input.Count, i == input.Count ? "done" : null);
            await Task.Delay(input.Delay, context.CancellationToken);
        }

        IAppExecutionContext current = HackExecutionContext.Current;
        return new SumResult(
            total,
            current.GetType().Name == "RemoteAppExecutionContext" ? "worker" : "main",
            context.ProcessId,
            current.Processes.OwnProcess.ParentPid?.Value ?? 0);
    }

    public static void Explode(int input, HackBackgroundContext context) => throw new InvalidOperationException("kaboom");

    public static async Task Forever(int input, HackBackgroundContext context)
    {
        while (true)
        {
            context.Report(0.5, "waiting");
            await Task.Delay(10, context.CancellationToken);
        }
    }

    /// <summary>Waits for the test to release it, ignoring its token: only a kill ends it early.</summary>
    public static async Task<int> Hold(int input, HackBackgroundContext context)
    {
        Volatile.Write(ref s_lastPid, context.ProcessId);
        Interlocked.Increment(ref s_started);
        while (!s_release)
        {
            await Task.Delay(10, CancellationToken.None);
        }

        return input;
    }

    public static int Undeclared(UndeclaredJobInput input, HackBackgroundContext context) => input.N;

    /// <summary>Counts characters, as a task class.</summary>
    public sealed class LengthJob : HackBackgroundJob<string, int>
    {
        public override Task<int> RunAsync(string input, HackBackgroundContext context) => Task.FromResult(input.Length);
    }

    // ---- the commands --------------------------------------------------------------------------

    public sealed class SumCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            ConcurrentQueue<HackBackgroundProgress> reports = new();
            SumResult result = await HackBackground.RunAsync<SumInput, SumResult>(
                Sum, new SumInput(int.Parse(context.Arguments[0]), 5),
                new SyncProgress(reports.Enqueue), cancellationToken: cancellationToken);
            await context.StandardOutput.WriteLineAsync($"total={result.Total}");
            await context.StandardOutput.WriteLineAsync($"where={result.Where}");
            bool child = result.Pid != context.App.ProcessId.Value && result.ParentPid == context.App.ProcessId.Value;
            await context.StandardOutput.WriteLineAsync($"child={(child ? "yes" : "no")}");
            await context.StandardOutput.WriteLineAsync($"progress={(reports.Count is > 0 and < 20 ? "yes" : "no:" + reports.Count)}");
            HackBackgroundProgress last = reports.Last();
            await context.StandardOutput.WriteLineAsync(FormattableString.Invariant($"last={last.Fraction:0.00} {last.Message}"));
            return 0;
        }
    }

    public sealed class ClassCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            int length = await HackBackground.RunAsync<LengthJob, string, int>(context.Arguments[0], cancellationToken: cancellationToken);
            await context.StandardOutput.WriteLineAsync(length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return 0;
        }
    }

    public sealed class FaultCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            try
            {
                await HackBackground.RunAsync<int>(Explode, 1, cancellationToken: cancellationToken);
                await context.StandardOutput.WriteLineAsync("no fault");
            }
            catch (HackBackgroundJobException exception)
            {
                string trace = string.IsNullOrEmpty(exception.OriginalStackTrace) ? "no" : "yes";
                await context.StandardOutput.WriteLineAsync($"{exception.OriginalTypeName}: {exception.Message} trace={trace}");
            }

            return 0;
        }
    }

    /// <summary><c>bg-forever</c>: runs <c>Forever</c> with no token of its own; only the task's stop request ends it.</summary>
    public sealed class ForeverCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            try
            {
                await HackBackground.RunAsync<int>(Forever, 0, cancellationToken: CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // The app is gone by then: nothing to print.
            }

            return 0;
        }
    }

    public sealed class CancelCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            using CancellationTokenSource cancel = new();
            try
            {
                await HackBackground.RunAsync<int>(
                    Forever, 0, new SyncProgress(_ => cancel.Cancel()), cancellationToken: cancel.Token);
                await context.StandardOutput.WriteLineAsync("ended");
            }
            catch (OperationCanceledException)
            {
                await context.StandardOutput.WriteLineAsync("cancelled");
            }

            return 0;
        }
    }

    /// <summary><c>bg-hold n [nocatch]</c>: starts n <c>Hold</c> tasks at once and waits for all.</summary>
    public sealed class HoldCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            int count = int.Parse(context.Arguments[0]);
            Task<int>[] tasks = [.. Enumerable.Range(0, count).Select(_ => HackBackground.RunAsync<int, int>(Hold, 1, options: new HackBackgroundOptions { Nice = 5 }, cancellationToken: cancellationToken))];
            if (context.Arguments.Count > 1)
            {
                await Task.WhenAll(tasks);
                return 0;
            }

            try
            {
                int[] results = await Task.WhenAll(tasks);
                await context.StandardOutput.WriteLineAsync($"held {results.Sum()}");
            }
            catch (HackProcessKilledException exception)
            {
                await context.StandardOutput.WriteLineAsync($"killed {exception.Reason}");
            }

            return 0;
        }
    }

    public sealed class UndeclaredCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            try
            {
                await HackBackground.RunAsync<UndeclaredJobInput, int>(Undeclared, new UndeclaredJobInput(1), cancellationToken: cancellationToken);
                await context.StandardOutput.WriteLineAsync("accepted");
            }
            catch (ArgumentException exception)
            {
                await context.StandardOutput.WriteLineAsync(
                    $"refused: {(exception.Message.Contains("JsonSerializable", StringComparison.Ordinal) ? "names JsonSerializable" : exception.Message)}");
            }

            return 0;
        }
    }

    /// <summary>Hands the OS a task of another assembly, as a forged request would (ADR 0128, section 2).</summary>
    public sealed class ForeignCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            try
            {
                await context.App.Background.RunAsync(
                    new HackBackgroundJobRequest(
                        new HackBackgroundJobTarget("Kazo.Os.Commands", "Kazo.Os.Commands.Cd.CdCommand", "Run", typeof(int).FullName!),
                        "1"),
                    null,
                    cancellationToken);
                await context.StandardOutput.WriteLineAsync("accepted");
            }
            catch (ArgumentException exception)
            {
                await context.StandardOutput.WriteLineAsync(
                    $"refused: {(exception.Message.Contains("ADR 0128", StringComparison.Ordinal) ? "names ADR 0128" : exception.Message)}");
            }

            return 0;
        }
    }

    /// <summary><c>bg-selfkill</c>: kills its own task, without <c>process.manage</c> (ADR 0140), and keeps running.</summary>
    public sealed class SelfKillCommand(AppManifest manifest) : TerminalAppBase(manifest)
    {
        public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
        {
            Task<int> task = HackBackground.RunAsync<int, int>(Hold, 1, cancellationToken: cancellationToken);
            while (Started == 0)
            {
                await Task.Delay(10, cancellationToken);
            }

            // The task's own pid, as the sample window reads it from a report (ListProcesses is main-thread only).
            await context.App.Processes.KillAsync(ProcessId.FromInt64(Volatile.Read(ref s_lastPid)), cancellationToken: cancellationToken);
            try
            {
                await task;
            }
            catch (HackProcessKilledException exception)
            {
                await context.StandardOutput.WriteLineAsync($"killed {exception.Reason}");
            }

            return 0;
        }
    }

    /// <summary>An <see cref="IProgress{T}"/> that reports on the caller's thread, without a synchronization context.</summary>
    private sealed class SyncProgress(Action<HackBackgroundProgress> report) : IProgress<HackBackgroundProgress>
    {
        public void Report(HackBackgroundProgress value) => report(value);
    }
}
