using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Metrics;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using static Kazo.Os.Platform.Core.Tests.FileSystem.Virtual.VirtualFileSystemFixture;

namespace Kazo.Os.Platform.Core.Tests.FileSystem.Virtual;

/// <summary>The measurement files of ADR 0057 and the counters behind them.</summary>
public sealed class ProcMetricsTests
{
    private const string Read = AppCapabilities.FileSystemSystemRead;

    // ── /proc/stat ────────────────────────────────────────────────────────

    [Fact]
    public async Task Stat_accumulates_user_and_system_time_per_core_in_user_hz()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        ProcessRecord app = Process(2, "org.tests.app");
        ProcessRecord service = Process(3, "org.tests.daemon", kind: AppKind.Service);
        fixture.Processes.Active.AddRange([app, service]);
        fixture.Processes.History.Add(Process(7, "org.tests.gone", ProcessState.Stopped));

        // Two seconds at 0.5 core of app time (pid 2 → cpu0) and 0.25 core of service time (pid 3 → cpu1).
        for (int second = 0; second < 2; second++)
        {
            fixture.Cpu.Record(
                [
                    new ProcessResourceSample(app.Pid, second, BootUtc, 0.5, 0, 0, 0),
                    new ProcessResourceSample(service.Pid, second, BootUtc, 0.25, 0, 0, 0)
                ],
                [app, service],
                TimeSpan.FromSeconds(1));
        }

        string stat = await fixture.ReadTextAsync("/proc/stat", fixture.AppContext(2, Read));

        Assert.Equal(
            "cpu  100 0 50 250 0 0 0 0 0 0\n" +
            "cpu0 100 0 0 100 0 0 0 0 0 0\n" +
            "cpu1 0 0 50 150 0 0 0 0 0 0\n" +
            $"btime {BootUtc.ToUnixTimeSeconds()}\n" +
            "processes 7\n" +
            "procs_running 2\n" +
            "procs_blocked 0\n",
            stat);
    }

    [Fact]
    public void Cpu_time_spills_onto_the_next_core_once_the_home_core_is_full()
    {
        CpuTimeAccounting cpu = new(new VirtualHardwareProfile("dual", 2, 1, 1, 1));
        ProcessRecord busy = Process(4, "org.tests.busy");

        cpu.Record([new ProcessResourceSample(busy.Pid, 0, BootUtc, 1.5, 0, 0, 0)], [busy], TimeSpan.FromSeconds(1));

        IReadOnlyList<CpuTimes> cores = cpu.GetCores();
        Assert.Equal(new CpuTimes(100, 0, 0, 0), cores[0]);
        Assert.Equal(new CpuTimes(50, 0, 0, 50), cores[1]);
    }

    [Fact]
    public void Measured_cpu_time_replaces_the_simulation_on_one_core()
    {
        MeasuredThread measured = new(new MeasuredThreadCpuTimes(
            TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.25), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10)));
        CpuTimeAccounting cpu = new(new VirtualHardwareProfile("dual", 2, 1, 1, 1), measured: measured);
        ProcessRecord busy = Process(4, "org.tests.busy");

        // The scheduler tick no longer invents time: ADR 0067.
        cpu.Record([new ProcessResourceSample(busy.Pid, 0, BootUtc, 1.5, 0, 0, 0)], [busy], TimeSpan.FromSeconds(1));

        IReadOnlyList<CpuTimes> cores = cpu.GetCores();
        Assert.Equal(new CpuTimes(150, 0, 50, 800), cores[0]);
        Assert.Equal(new CpuTimes(0, 0, 0, 1000), cores[1]);
    }

    [Fact]
    public void A_null_accountant_keeps_the_simulated_cpu_time()
    {
        CpuTimeAccounting cpu = new(new VirtualHardwareProfile("dual", 2, 1, 1, 1), measured: Kazo.Os.Platform.Core.Processes.Accounting.NullCpuTimeAccountant.Instance);
        ProcessRecord busy = Process(4, "org.tests.busy");

        cpu.Record([new ProcessResourceSample(busy.Pid, 0, BootUtc, 1.5, 0, 0, 0)], [busy], TimeSpan.FromSeconds(1));

        Assert.Equal(new CpuTimes(100, 0, 0, 0), cpu.GetCores()[0]);
    }

    private sealed class MeasuredThread(MeasuredThreadCpuTimes thread) : ICpuTimeAccountant
    {
        public bool IsMeasuring => true;

        public bool TryGetCpuUsage(ProcessId pid, out double coreEquivalents)
        {
            coreEquivalents = 0;
            return false;
        }

        public bool TryGetProcessTimes(ProcessId pid, out ProcessCpuTimes times)
        {
            times = default;
            return false;
        }

        public bool TryGetThreadTimes(out MeasuredThreadCpuTimes times)
        {
            times = thread;
            return true;
        }
    }

    [Fact]
    public void Sampling_service_ticks_the_simulator_once_per_simulated_second()
    {
        global::Kazo.Os.Platform.Core.Time.ManualSimulationClock clock = new(BootUtc, TimeSpan.FromMilliseconds(100));
        DeterministicResourceSimulator simulator = new(clock, new global::Kazo.Os.Platform.Core.Time.SeededSimulationRandom(1), Hardware);
        CpuTimeAccounting cpu = new(Hardware);
        ProcessIoAccounting io = new();
        FakeProcessManager processes = new();
        ProcessRecord running = Process(2, "org.tests.app");
        processes.Active.Add(running);
        io.RecordRead(ProcessId.FromInt64(99), 10, fromStorage: true);
        using ResourceSamplingService sampling = new(clock, simulator, () => processes, cpu, io);

        sampling.Start();
        clock.Advance(25);

        Assert.Equal(2, simulator.GetSystemHistory().Count);
        Assert.Equal(2, simulator.GetHistory(running.Pid).Count);
        // Every core got 100 units per sampled second, split between busy and idle.
        Assert.All(cpu.GetCores(), core => Assert.InRange(core.User + core.System + core.Idle, 198, 200));
        // The exited process 99 lost its I/O row at the first pass.
        Assert.Equal(ProcessIoCounters.Zero, io.Get(ProcessId.FromInt64(99)));
    }

    // ── /proc/<pid>/io ────────────────────────────────────────────────────

    [Fact]
    public async Task Io_counts_what_the_process_really_reads_and_writes_through_the_filesystem()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        fixture.Processes.Active.Add(Process(42, "org.tests.app"));
        FileSystemAuthorizationContext alice = fixture.AppContext(42, AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite, Read);
        VirtualPath file = VirtualPath.Parse("/home/alice/notes.txt");
        await fixture.Service.CreateAsync(new FileSystemCreateRequest(file, FileSystemEntryKind.File, FileSystemPermissions.FromMode(0x1A4)), alice);
        await fixture.Service.WriteAsync(new FileSystemWriteRequest(file), new BytesSource(new byte[3000]), alice);

        string content = await fixture.ReadTextAsync(file.Value, alice);
        string io = await fixture.ReadTextAsync("/proc/42/io", alice);

        Assert.Equal(3000, content.Length);
        Dictionary<string, long> fields = ParseIo(io);
        Assert.Equal(
            ["rchar", "wchar", "syscr", "syscw", "read_bytes", "write_bytes", "cancelled_write_bytes"],
            fields.Keys);
        Assert.Equal(1, fields["syscw"]);
        Assert.Equal(3000, fields["wchar"]);
        Assert.Equal(3000, fields["write_bytes"]);
        Assert.True(fields["syscr"] >= 2, "The read loop issues at least one data read and one end-of-file read.");
        // /proc/42/io is rendered when opened, so reading it does not count in its own numbers.
        Assert.Equal(3000, fields["read_bytes"]);
        Assert.Equal(3000, fields["rchar"]);
        Assert.Equal(0, fields["cancelled_write_bytes"]);
    }

    [Fact]
    public async Task Io_of_proc_and_dev_counts_characters_but_not_storage_bytes()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        fixture.Processes.Active.Add(Process(42, "org.tests.app"));
        FileSystemAuthorizationContext alice = fixture.AppContext(42, Read);

        string uptime = await fixture.ReadTextAsync("/proc/uptime", alice);
        ProcessIoCounters counters = fixture.ProcessIo.Get(ProcessId.FromInt64(42));

        Assert.Equal(uptime.Length, counters.ReadChars);
        Assert.Equal(0, counters.ReadBytes);
    }

    [Fact]
    public async Task Io_is_private_to_its_owner_like_environ()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        LocalUserId bob = LocalUserId.FromGuid(Guid.NewGuid());
        fixture.Processes.Active.Add(Process(42, "org.tests.app"));
        fixture.Processes.Active.Add(Process(43, "org.tests.other", userId: bob));
        FileSystemAuthorizationContext alice = fixture.AppContext(42, Read, AppCapabilities.ProcessList);

        FileSystemResult<FileSystemEntrySnapshot> own = await fixture.Service.StatAsync(
            new FileSystemStatRequest(VirtualPath.Parse("/proc/42/io")), alice);
        FileSystemResult<FileSystemContentReadHandle> other = await fixture.Service.ReadAsync(
            new FileSystemReadRequest(VirtualPath.Parse("/proc/43/io")), alice);

        Assert.Equal(0x100, own.Value!.Metadata.Permissions.Mode); // 0400
        Assert.False(other.Succeeded);
        Assert.Equal(FileSystemErrorCode.PermissionDenied, other.Error!.Code);
        Assert.Contains("rchar: 0\n", await fixture.ReadTextAsync("/proc/43/io", fixture.SystemContext()));
    }

    // ── /proc/diskstats ───────────────────────────────────────────────────

    [Fact]
    public async Task Diskstats_reports_hda_in_the_kernel_layout()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        fixture.Disk.Begin();
        fixture.Disk.Complete(BlockIoDirection.Read, 4096, TimeSpan.FromMilliseconds(3), succeeded: true);
        fixture.Disk.Begin();
        fixture.Disk.Complete(BlockIoDirection.Write, 1000, TimeSpan.FromMilliseconds(5), succeeded: true);
        fixture.Disk.Begin();
        fixture.Disk.Complete(BlockIoDirection.Write, 1000, TimeSpan.FromMilliseconds(5), succeeded: false);

        string diskstats = await fixture.ReadTextAsync("/proc/diskstats", fixture.SystemContext());

        string[] fields = diskstats.TrimEnd('\n').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("   8       0 hda ", diskstats);
        Assert.Equal(14, fields.Length);
        Assert.Equal(["1", "0", "8", "3", "1", "0", "2", "5", "0"], fields[3..12]);
    }

    [Fact]
    public void Block_statistics_count_busy_time_weighted_by_queue_depth()
    {
        SteppingTimeProvider time = new();
        BlockDeviceStatistics disk = new(time);

        disk.Begin();
        time.Advance(TimeSpan.FromMilliseconds(10));
        disk.Begin();
        time.Advance(TimeSpan.FromMilliseconds(10));
        disk.Complete(BlockIoDirection.Read, 512, TimeSpan.FromMilliseconds(20), succeeded: true);
        BlockDeviceCounters during = disk.GetCounters();
        time.Advance(TimeSpan.FromMilliseconds(10));
        disk.Complete(BlockIoDirection.Read, 512, TimeSpan.FromMilliseconds(20), succeeded: true);
        time.Advance(TimeSpan.FromMilliseconds(50));
        BlockDeviceCounters after = disk.GetCounters();

        Assert.Equal(1, during.IosInProgress);
        Assert.Equal(0, after.IosInProgress);
        Assert.Equal(30, after.MillisecondsDoingIo); // idle time after the last completion is not busy time
        Assert.Equal(40, after.WeightedMillisecondsDoingIo); // 10 × 1 + 10 × 2 + 10 × 1
    }

    // ── /proc/net/dev ─────────────────────────────────────────────────────

    [Fact]
    public async Task Net_dev_lists_lo_and_eth0_under_the_kernel_header()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();
        fixture.Network.RecordTransmit(NetworkInterfaceNames.Ethernet, 120);
        fixture.Network.RecordReceive(NetworkInterfaceNames.Ethernet, 4000);
        fixture.Network.RecordReceiveError(NetworkInterfaceNames.Ethernet);
        fixture.Network.RecordTransmit("wlan9", 1); // unknown device: dropped

        string netDev = await fixture.ReadTextAsync("/proc/net/dev", fixture.AppContext(null, Read));
        FileSystemResult<FileSystemDirectorySnapshot> net = await fixture.Service.EnumerateAsync(
            new FileSystemEnumerateRequest(VirtualPath.Parse("/proc/net")), fixture.SystemContext());

        string[] lines = netDev.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Equal("Inter-|   Receive                                                |  Transmit", lines[0]);
        Assert.Equal("    lo:       0       0    0    0    0     0          0         0        0       0    0    0    0     0       0          0", lines[2]);
        Assert.Equal("  eth0:    4000       1    1    0    0     0          0         0      120       1    0    0    0     0       0          0", lines[3]);
        Assert.Equal(["dev", "tcp"], net.Value!.Entries.Select(static entry => entry.Name.Value));
    }

    // ── /proc/mounts ──────────────────────────────────────────────────────

    [Fact]
    public async Task Mounts_lists_every_router_mount_in_fstab_format()
    {
        VirtualFileSystemFixture fixture = await CreateSeededAsync();

        string mounts = await fixture.ReadTextAsync("/proc/mounts", fixture.AppContext(null, Read));

        Assert.Equal(
            "/dev/hda / idbfs rw 0 0\n" +
            "devfs /dev devfs rw 0 0\n" +
            "proc /proc proc ro 0 0\n",
            mounts);
    }

    private static Dictionary<string, long> ParseIo(string io) =>
        io.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Split(": "))
            .ToDictionary(static parts => parts[0], static parts => long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>A monotonic time source that only moves when a test says so.</summary>
    private sealed class SteppingTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan delta) => _ticks += delta.Ticks;
    }
}
