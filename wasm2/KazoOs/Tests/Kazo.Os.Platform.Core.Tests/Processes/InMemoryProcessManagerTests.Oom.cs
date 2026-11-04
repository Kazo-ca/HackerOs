using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Tests.Processes;

/// <summary>
/// ADR 0126: <c>oom_score_adj</c> (inheritance and its floor), the OOM killer's choice and traces,
/// its threshold and two-sample rule, the "only where memory is measured" rule, and the browser guard.
/// </summary>
public sealed partial class InMemoryProcessManagerTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void A_child_inherits_oom_score_adj_and_only_a_privileged_writer_lowers_it_under_its_floor()
    {
        Fixture fixture = new();
        InMemoryProcessOomScoreTable table = fixture.Manager.OomScores;
        ProcessRecord shell = fixture.Manager.Start(fixture.RequestFor("com.kazoos.terminal"));
        Assert.Equal(0, table.GetOrDefault(shell.Pid));

        // The owner may raise its own value, and go back down to where it started (the floor).
        Assert.Equal(ResourceLimitChangeResult.Applied, table.TrySet(shell.Pid, 300, privileged: false));
        Assert.Equal(ResourceLimitChangeResult.Applied, table.TrySet(shell.Pid, 0, privileged: false));
        // Under the floor needs CAP_SYS_RESOURCE; out of [-1000, 1000] is EINVAL.
        Assert.Equal(ResourceLimitChangeResult.PermissionDenied, table.TrySet(shell.Pid, -1, privileged: false));
        Assert.Equal(ResourceLimitChangeResult.InvalidArgument, table.TrySet(shell.Pid, 1001, privileged: true));
        Assert.Equal(ResourceLimitChangeResult.NoSuchProcess, table.TrySet(ProcessId.FromInt64(99999), 0, privileged: true));
        Assert.Equal(ResourceLimitChangeResult.Applied, table.TrySet(shell.Pid, 200, privileged: false));

        ProcessRecord child = fixture.Manager.Start(fixture.RequestFor("com.kazoos.child", shell.Pid));
        Assert.Equal(200, table.GetOrDefault(child.Pid));

        // A declared value (a system service's -500) replaces the parent's.
        ProcessRecord service = fixture.Manager.Start(fixture.RequestFor("com.kazoos.svc", shell.Pid) with { OomScoreAdjust = -500 });
        Assert.Equal(-500, table.GetOrDefault(service.Pid));
        Assert.Equal(ResourceLimitChangeResult.PermissionDenied, table.TrySet(service.Pid, -600, privileged: false));
        Assert.Equal(ResourceLimitChangeResult.Applied, table.TrySet(service.Pid, -600, privileged: true));

        fixture.Manager.Kill(child.Pid);
        Assert.False(table.TryGet(child.Pid, out _));
    }

    [Fact]
    public void Oom_score_is_rss_per_mille_of_memtotal_plus_the_adjustment_bounded_to_0_2000()
    {
        OomHarness harness = new(totalMiB: 1000);

        Assert.Equal(250, harness.Probe.OomScore(250 * MiB, 0));
        Assert.Equal(750, harness.Probe.OomScore(250 * MiB, 500));
        Assert.Equal(0, harness.Probe.OomScore(250 * MiB, -999));
        Assert.Equal(0, harness.Probe.OomScore(900 * MiB, -1000));
        Assert.Equal(2000, harness.Probe.OomScore(2000 * MiB, 1000));
    }

    [Fact]
    public void Off_by_default_the_oom_killer_never_kills()
    {
        OomHarness harness = new(totalMiB: 1000);
        ProcessRecord hog = harness.Start("org.kazoos.commands.stress", residentMiB: 990);

        Assert.False(harness.Killer.IsActive);
        Assert.Null(harness.Killer.CheckNow());
        Assert.Null(harness.Killer.CheckNow());
        Assert.True(harness.Fixture.Manager.TryGetActive(hog.Pid, out _));
        Assert.Equal(0, harness.Killer.KillCount);
    }

    [Fact]
    public void Two_low_samples_in_a_row_kill_the_highest_oom_score_and_leave_linux_s_traces()
    {
        OomHarness harness = new(totalMiB: 1000);
        ProcessRecord editor = harness.Start("org.kazoos.text-editor", residentMiB: 100, kind: AppKind.Window);
        ProcessRecord hog = harness.Start("org.kazoos.commands.stress", residentMiB: 880);
        harness.Killer.Configure(new OomKillerSettings(true, 5));
        Assert.Contains(harness.Log.ReadAll(), static entry => entry.Message == "oom-killer: enabled, min-free-percent=5");

        // 980 MiB used of 1000: 2 % free, under 5 %. One sample is not enough.
        Assert.Null(harness.Killer.CheckNow());
        OomKill kill = Assert.IsType<OomKill>(harness.Killer.CheckNow());

        Assert.Equal(hog.Pid, kill.Pid);
        Assert.Equal(OomKillCause.SystemMemory, kill.Cause);
        Assert.Equal(880, kill.Score);
        Assert.Equal(1, harness.Killer.KillCount);
        ProcessRecord dead = harness.Fixture.Manager.GetHistory().Last(record => record.Pid == hog.Pid);
        Assert.Equal(ProcessExitReason.OutOfMemory, dead.ExitReason);
        Assert.True(harness.Fixture.Manager.TryGetActive(editor.Pid, out _));

        string[] lines = [.. harness.Log.ReadAll().Select(static entry => entry.Message)];
        Assert.Contains(lines, line => line.StartsWith("oom-kill:constraint=CONSTRAINT_NONE", StringComparison.Ordinal)
            && line.Contains($"task=stress,pid={hog.Pid.Value}", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith($"Out of memory: Killed process {hog.Pid.Value} (stress) total-vm:{880 * 1024}kB", StringComparison.Ordinal)
            && line.EndsWith("oom_score_adj:0", StringComparison.Ordinal));
        // A command has no window: nobody is notified.
        Assert.Empty(harness.Notifier.Killed);
    }

    [Fact]
    public void Oom_score_adj_steers_the_choice_and_minus_1000_is_never_chosen()
    {
        OomHarness harness = new(totalMiB: 1000);
        ProcessRecord big = harness.Start("org.kazoos.daemon", residentMiB: 700);
        ProcessRecord small = harness.Start("org.kazoos.kazo-paint", residentMiB: 280, kind: AppKind.Window);
        harness.Fixture.Manager.OomScores.TrySet(big.Pid, -1000, privileged: true);
        harness.Fixture.Manager.OomScores.TrySet(small.Pid, 100, privileged: false);
        harness.Killer.Configure(new OomKillerSettings(true, 5));

        harness.Killer.CheckNow();
        OomKill kill = Assert.IsType<OomKill>(harness.Killer.CheckNow());

        Assert.Equal(small.Pid, kill.Pid);
        Assert.True(harness.Fixture.Manager.TryGetActive(big.Pid, out _));
        // A window app's user learns why its window vanished.
        Assert.Equal([small.Pid], harness.Notifier.Killed);
    }

    [Fact]
    public void Memory_coming_back_between_two_samples_resets_the_count()
    {
        OomHarness harness = new(totalMiB: 1000);
        ProcessRecord hog = harness.Start("org.kazoos.commands.stress", residentMiB: 980);
        harness.Killer.Configure(new OomKillerSettings(true, 5));

        Assert.Null(harness.Killer.CheckNow());
        harness.Memory.Resident[hog.Pid.Value] = 500 * MiB;
        Assert.Null(harness.Killer.CheckNow());
        harness.Memory.Resident[hog.Pid.Value] = 980 * MiB;
        Assert.Null(harness.Killer.CheckNow());
        Assert.NotNull(harness.Killer.CheckNow());
    }

    [Fact]
    public void Enabled_where_memory_is_not_measured_it_says_so_once_and_never_kills()
    {
        OomHarness harness = new(totalMiB: 1000, measuring: false);
        harness.Start("org.kazoos.commands.stress", residentMiB: 990);

        harness.Killer.Configure(new OomKillerSettings(true, 5));
        harness.Killer.Configure(new OomKillerSettings(true, 10));

        Assert.False(harness.Killer.IsActive);
        Assert.Single(harness.Log.ReadAll(), static entry => entry.Message == "oom-killer: disabled, memory is not measured");
        Assert.Null(harness.Killer.CheckNow());
        Assert.Null(harness.Killer.CheckNow());
    }

    [Fact]
    public void The_browser_guard_warns_once_per_runtime_and_kills_only_in_that_runtime_when_active()
    {
        OomHarness harness = new(totalMiB: 4000);
        ProcessRecord main = harness.Start("org.kazoos.text-editor", residentMiB: 900, kind: AppKind.Window);
        ProcessRecord inWorker = harness.Start("org.kazoos.commands.stress", residentMiB: 300);
        harness.Runtimes.Readings.Add(new RuntimeMemoryReading("worker-2", "kazoos-worker-2", 1700 * MiB, 2048 * MiB, [inWorker.Pid.Value]));

        // Off: the warning only, once.
        Assert.Null(harness.Killer.CheckNow());
        Assert.Null(harness.Killer.CheckNow());
        Assert.Single(harness.Log.ReadAll(), static entry => entry.Message.StartsWith("kazoos-worker-2: memory", StringComparison.Ordinal));
        Assert.Single(harness.Notifier.NearLimit);

        harness.Killer.Configure(new OomKillerSettings(true, 5));
        OomKill kill = Assert.IsType<OomKill>(harness.Killer.CheckNow());

        // The worker's process, though the editor scores higher system-wide.
        Assert.Equal(inWorker.Pid, kill.Pid);
        Assert.Equal(OomKillCause.BrowserLimit, kill.Cause);
        Assert.Equal("worker-2", kill.RuntimeId);
        Assert.True(harness.Fixture.Manager.TryGetActive(main.Pid, out _));
        Assert.Equal([inWorker.Pid.Value], harness.Runtimes.Released);
        Assert.Contains(harness.Log.ReadAll(), entry =>
            entry.Message.StartsWith($"Memory cgroup out of memory: Killed process {inWorker.Pid.Value} (stress)", StringComparison.Ordinal));
        Assert.Single(harness.Notifier.NearLimit);
    }

    [Fact]
    public void The_memory_document_brings_an_out_of_range_threshold_back_with_a_warning()
    {
        LayeredConfigSnapshot snapshot = new(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MemorySettingsDocuments.EnabledQualifiedKey] = "true",
                [MemorySettingsDocuments.MinFreePercentQualifiedKey] = "80"
            },
            new Dictionary<string, SettingLayer>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            []);

        OomKillerSettings settings = MemorySettingsDocuments.ToSettings(snapshot, out string? warning);

        Assert.Equal(new OomKillerSettings(true, 50), settings);
        Assert.Contains("out of range 0-50", warning, StringComparison.Ordinal);
        Assert.Contains("[oom-killer]\nenabled=false\nmin-free-percent=5\n", MemorySettingsDocuments.DefaultContent, StringComparison.Ordinal);
        Assert.True(OsSettingsDocumentRegistry.TryGet("memory", out OsSettingsDocument document));
        Assert.True(document.SystemOnly);
    }

    [Fact]
    public void The_kernel_ring_buffer_formats_dev_kmsg_and_keeps_its_last_messages()
    {
        ManualSimulationClock clock = new(new DateTimeOffset(2026, 10, 18, 7, 0, 0, TimeSpan.Zero), TimeSpan.FromMilliseconds(100));
        KernelLog log = new(clock);
        clock.Advance(125);
        log.Write(KernelLogLevel.Error, "Out of memory:\nKilled");

        Assert.Equal("3,0,12500000,-;Out of memory: Killed\n", log.FormatDevKmsg());
        Assert.Equal("[   12.500000]", KernelLog.FormatStamp(TimeSpan.FromSeconds(12.5)));

        for (int index = 0; index < KernelLog.Capacity + 5; index++)
        {
            log.Write(KernelLogLevel.Info, $"line {index}");
        }

        IReadOnlyList<KernelLogEntry> entries = log.ReadAll();
        Assert.Equal(KernelLog.Capacity, entries.Count);
        Assert.Equal($"line {KernelLog.Capacity + 4}", entries[^1].Message);
    }

    /// <summary>A process table whose memory is measured by a controllable accountant, and the OOM killer over it.</summary>
    private sealed class OomHarness
    {
        internal OomHarness(long totalMiB, bool measuring = true)
        {
            Fixture = new Fixture();
            VirtualHardwareProfile hardware = new("oom-rig", 1, totalMiB * MiB, 1, 1);
            Memory = new ControlledMemoryAccountant(measuring);
            Probe = new SystemMemoryProbe(
                () => Fixture.Manager,
                new DeterministicResourceSimulator(Fixture.Clock, new SeededSimulationRandom(1), hardware),
                hardware,
                Memory);
            Log = new KernelLog(Fixture.Clock);
            Killer = new OomKiller(Fixture.Clock, () => Fixture.Manager, Probe, () => Fixture.Manager.OomScores, Log, Runtimes, Notifier);
        }

        internal Fixture Fixture { get; }

        internal ControlledMemoryAccountant Memory { get; }

        internal SystemMemoryProbe Probe { get; }

        internal KernelLog Log { get; }

        internal OomKiller Killer { get; }

        internal FakeRuntimes Runtimes { get; } = new();

        internal RecordingNotifier Notifier { get; } = new();

        internal ProcessRecord Start(string appId, long residentMiB, AppKind kind = AppKind.Terminal)
        {
            ProcessRecord process = Fixture.Manager.Start(Fixture.RequestFor(appId) with { Kind = kind });
            Memory.Resident[process.Pid.Value] = residentMiB * MiB;
            return process;
        }
    }

    /// <summary>Measured memory set by the test: each process's anonymous memory, and their sum as the system's.</summary>
    private sealed class ControlledMemoryAccountant(bool measuring) : IMemoryAccountant
    {
        internal Dictionary<long, long> Resident { get; } = [];

        public bool IsMeasuring => measuring;

        public bool TryGetProcessMemory(ProcessRecord process, IReadOnlyList<ProcessRecord> active, out ProcessMemoryUsage usage)
        {
            usage = new ProcessMemoryUsage(0, 0, Resident.GetValueOrDefault(process.Pid.Value), 0, 0);
            return measuring && Resident.ContainsKey(process.Pid.Value);
        }

        public bool TryGetSystemMemory(IReadOnlyList<ProcessRecord> active, out SystemMemoryUsage usage)
        {
            long used = active.Sum(process => Resident.GetValueOrDefault(process.Pid.Value));
            usage = new SystemMemoryUsage(used, 0, 0, active.Count);
            return measuring;
        }
    }

    private sealed class FakeRuntimes : IRuntimeMemorySource
    {
        internal List<RuntimeMemoryReading> Readings { get; } = [];

        internal List<long> Released { get; } = [];

        public IReadOnlyList<RuntimeMemoryReading> Read() => Readings;

        public void Collect(string runtimeId)
        {
        }

        public void ReleaseProcess(long pid) => Released.Add(pid);
    }

    private sealed class RecordingNotifier : IOomNotifier
    {
        internal List<ProcessId> Killed { get; } = [];

        internal List<string> NearLimit { get; } = [];

        public void ProcessKilled(ProcessRecord process) => Killed.Add(process.Pid);

        public void RuntimeNearLimit(RuntimeMemoryReading runtime) => NearLimit.Add(runtime.Id);
    }
}
