using Kazo.Os.Platform.WorkerHost.Host;
using Kazo.Os.Simulation.Abstractions.Workers;

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>
/// What a cross-origin isolated page adds to the workers (ADR 0131, section 4; ADR 0143): synchronous
/// gateway calls (a worker waits for the OS like a blocking system call, here on the loopback's
/// event instead of <c>Atomics.wait</c>) and the browser's memory measurement of each worker.
/// </summary>
public sealed class SynchronousCallTests
{
    [Fact]
    public async Task Without_isolation_a_synchronous_process_list_is_refused_in_a_worker()
    {
        await using OsFixture os = await StartAsync(synchronousCalls: false);

        ShellRun run = await os.RunAsync("sample-cmd --processes").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("ListProcesses", run.Stderr, StringComparison.Ordinal);
        Assert.Equal(string.Empty, run.Stdout);
    }

    [Fact]
    public async Task With_isolation_a_worker_lists_processes_synchronously()
    {
        await using OsFixture os = await StartAsync(synchronousCalls: true);

        ShellRun run = await os.RunAsync("sample-cmd --processes").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(run.ExitCode == 0, run.Stderr);
        long samplePid = os.Manager.GetHistory().Last(p => p.AppId.Contains("sample", StringComparison.OrdinalIgnoreCase)).Pid.Value;
        Assert.StartsWith("processes: ", run.Stdout, StringComparison.Ordinal);
        Assert.Contains(samplePid.ToString(System.Globalization.CultureInfo.InvariantCulture), run.Stdout.Split(' ', StringSplitOptions.TrimEntries));
    }

    [Fact]
    public async Task The_same_command_in_process_needs_no_isolation()
    {
        await using OsFixture os = await OsFixture.CreateAsync(workers: false);

        ShellRun run = await os.RunAsync("sample-cmd --processes").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(run.ExitCode == 0, run.Stderr);
        Assert.StartsWith("processes: ", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_synchronous_kill_of_another_process_gets_the_kernels_answer()
    {
        await using OsFixture os = await StartAsync(synchronousCalls: true);

        // pid 1 belongs to the system: the OS refuses, and the refusal travels back on the synchronous answer.
        ShellRun run = await os.RunAsync("sample-cmd --kill 1").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, run.ExitCode);
        Assert.StartsWith("sample-cmd: ", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("not available to a process running in a worker", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_isolation_a_synchronous_kill_of_another_process_is_refused()
    {
        await using OsFixture os = await StartAsync(synchronousCalls: false);

        ShellRun run = await os.RunAsync("sample-cmd --kill 1").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Kill of another process", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pipe_through_a_synchronous_capable_worker_keeps_its_order()
    {
        await using OsFixture os = await StartAsync(synchronousCalls: true);

        ShellRun run = await os.RunAsync("seq 3000 | wc -l").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal("3000", run.Stdout.Trim());
    }

    [Fact]
    public async Task The_browsers_memory_measurement_replaces_the_heartbeat_estimate()
    {
        MeteredRuntime? metered = null;
        await using OsFixture os = await OsFixture.CreateAsync(
            workers: true,
            poolOptions: OsFixture.FastOptions with { MemoryMeasurePeriod = TimeSpan.FromMilliseconds(50) },
            wrapRuntime: inner => metered = new MeteredRuntime(inner));
        os.Startup.MarkStarted();

        await OsFixture.WaitUntilAsync(
            () => os.Pool!.Connections.Any(static worker => worker.IsMemoryMeasuredByBrowser),
            TimeSpan.FromSeconds(10),
            "a worker measured by the browser");

        WorkerConnection measured = os.Pool!.Connections.First(static worker => worker.IsMemoryMeasuredByBrowser);
        Assert.Equal(MeteredRuntime.Bytes, measured.RuntimeMemoryBytes);
        Assert.True(metered!.Measurements > 0);
        ShellRun run = await os.RunAsync("cat /proc/hackeros/workers").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains($" {MeteredRuntime.Bytes / 1024} ", run.Stdout, StringComparison.Ordinal);
    }

    private static async Task<OsFixture> StartAsync(bool synchronousCalls)
    {
        OsFixture os = await OsFixture.CreateAsync(workers: true, synchronousCalls: synchronousCalls);
        os.Startup.MarkStarted();
        return os;
    }

    /// <summary>A loopback runtime whose "browser" measures every worker at 48 MiB.</summary>
    private sealed class MeteredRuntime(IWebWorkerRuntime inner) : IWebWorkerRuntime, IWebWorkerMemoryMeter
    {
        public const long Bytes = 48L * 1024 * 1024;

        private readonly List<int> _started = [];
        private int _measurements;

        public int Measurements => Volatile.Read(ref _measurements);

        public int HardwareConcurrency => inner.HardwareConcurrency;

        public async Task<IWebWorker> StartAsync(WebWorkerStartOptions options, CancellationToken cancellationToken = default)
        {
            IWebWorker worker = await inner.StartAsync(options, cancellationToken);
            lock (_started)
            {
                _started.Add(worker.Id);
            }

            return worker;
        }

        public Task WhenIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            inner.WhenIdleAsync(timeout, cancellationToken);

        public Task<IReadOnlyDictionary<int, long>?> MeasureWorkersAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _measurements);
            lock (_started)
            {
                return Task.FromResult<IReadOnlyDictionary<int, long>?>(_started.ToDictionary(static id => id, static _ => Bytes));
            }
        }
    }
}
