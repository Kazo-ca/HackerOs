using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Processes;

public sealed class DeterministicResourceSimulatorTests
{
    private static readonly DateTimeOffset StartUtc = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private static ProcessRecord CreateRunningProcess(long pid, ResourceProfile profile) => new(
        ProcessId.FromInt64(pid),
        parentPid: null,
        "com.hackeros.worker",
        AppInstanceId.FromGuid(Guid.NewGuid()),
        AppKind.Service,
        LocalUserId.FromGuid(Guid.NewGuid()),
        SessionId.FromGuid(Guid.NewGuid()),
        ProcessState.Running,
        profile,
        serviceHealth: ServiceHealth.Healthy,
        StartUtc,
        startedAtUtc: StartUtc,
        stoppedAtUtc: null,
        exitCode: null,
        exitReason: null);

    private static ProcessRecord CreateStoppedProcess(long pid, ResourceProfile profile) => new(
        ProcessId.FromInt64(pid),
        parentPid: null,
        "com.hackeros.worker",
        AppInstanceId.FromGuid(Guid.NewGuid()),
        AppKind.Service,
        LocalUserId.FromGuid(Guid.NewGuid()),
        SessionId.FromGuid(Guid.NewGuid()),
        ProcessState.Stopped,
        profile,
        serviceHealth: ServiceHealth.Healthy,
        StartUtc,
        startedAtUtc: StartUtc,
        stoppedAtUtc: StartUtc,
        exitCode: 0,
        exitReason: ProcessExitReason.Completed);

    private static (ManualSimulationClock Clock, SeededSimulationRandom Random) CreateDeterministicSources() =>
        (new ManualSimulationClock(StartUtc, TimeSpan.FromSeconds(1)), new SeededSimulationRandom(rootSeed: 42));

    [Fact]
    public void A_stopped_process_consumes_zero_resources()
    {
        (ManualSimulationClock clock, SeededSimulationRandom random) = CreateDeterministicSources();
        DeterministicResourceSimulator simulator = new(clock, random, VirtualHardwareProfile.Default);
        ResourceProfile profile = new(0.5, 1.0, 0.5, 1.0, 0.5, 1.0, 0.5, 1.0);
        ProcessRecord stopped = CreateStoppedProcess(1, profile);

        IReadOnlyList<ProcessResourceSample> samples = simulator.Tick([stopped]);

        Assert.Empty(samples);
    }

    [Fact]
    public void A_running_process_with_a_nonzero_profile_reports_positive_usage()
    {
        (ManualSimulationClock clock, SeededSimulationRandom random) = CreateDeterministicSources();
        DeterministicResourceSimulator simulator = new(clock, random, VirtualHardwareProfile.Default);
        ResourceProfile profile = new(0.3, 0.8, 0.3, 0.8, 0.3, 0.8, 0.3, 0.8);
        ProcessRecord running = CreateRunningProcess(1, profile);

        IReadOnlyList<ProcessResourceSample> samples = simulator.Tick([running]);

        ProcessResourceSample sample = Assert.Single(samples);
        Assert.True(sample.CpuUsage > 0);
        Assert.True(sample.MemoryUsageBytes > 0);
        Assert.True(sample.StorageIoBytes > 0);
        Assert.True(sample.NetworkIoBytes > 0);
    }

    [Fact]
    public void The_zero_resource_profile_always_reports_zero_usage()
    {
        (ManualSimulationClock clock, SeededSimulationRandom random) = CreateDeterministicSources();
        DeterministicResourceSimulator simulator = new(clock, random, VirtualHardwareProfile.Default);
        ProcessRecord running = CreateRunningProcess(1, ResourceProfile.None);

        ProcessResourceSample sample = Assert.Single(simulator.Tick([running]));

        Assert.Equal(0, sample.CpuUsage);
        Assert.Equal(0, sample.MemoryUsageBytes);
        Assert.Equal(0, sample.StorageIoBytes);
        Assert.Equal(0, sample.NetworkIoBytes);
    }

    [Fact]
    public void Identical_seeds_and_ticks_produce_identical_usage_sequences()
    {
        ResourceProfile profile = new(0.2, 0.9, 0.2, 0.9, 0.2, 0.9, 0.2, 0.9);
        ProcessId pid = ProcessId.FromInt64(1);

        List<ProcessResourceSample> RunThreeTicks()
        {
            ManualSimulationClock clock = new(StartUtc, TimeSpan.FromSeconds(1));
            SeededSimulationRandom random = new(rootSeed: 7);
            DeterministicResourceSimulator simulator = new(clock, random, VirtualHardwareProfile.Default);
            ProcessRecord process = CreateRunningProcess(1, profile);

            List<ProcessResourceSample> collected = [];
            for (int i = 0; i < 3; i++)
            {
                collected.AddRange(simulator.Tick([process]));
                clock.Advance();
            }

            return collected;
        }

        List<ProcessResourceSample> first = RunThreeTicks();
        List<ProcessResourceSample> second = RunThreeTicks();

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].CpuUsage, second[i].CpuUsage);
            Assert.Equal(first[i].MemoryUsageBytes, second[i].MemoryUsageBytes);
            Assert.Equal(first[i].StorageIoBytes, second[i].StorageIoBytes);
            Assert.Equal(first[i].NetworkIoBytes, second[i].NetworkIoBytes);
        }
    }

    [Fact]
    public void Aggregate_cpu_usage_across_processes_never_exceeds_hardware_capacity()
    {
        (ManualSimulationClock clock, SeededSimulationRandom random) = CreateDeterministicSources();
        VirtualHardwareProfile hardware = new("small", cpuCapacity: 1.0, memoryCapacityBytes: 1_000_000, storageIoCapacityBytesPerTick: 1_000, networkIoCapacityBytesPerTick: 1_000);
        DeterministicResourceSimulator simulator = new(clock, random, hardware);
        ResourceProfile profile = new(1, 1, 1, 1, 1, 1, 1, 1);

        List<ProcessRecord> processes = Enumerable.Range(1, 10).Select(i => CreateRunningProcess(i, profile)).ToList();
        IReadOnlyList<ProcessResourceSample> samples = simulator.Tick(processes);

        double totalCpu = samples.Sum(s => s.CpuUsage);
        Assert.True(totalCpu <= hardware.CpuCapacity + 0.0001);
    }

    [Fact]
    public void Per_process_history_is_bounded_and_evicts_the_oldest_sample()
    {
        (ManualSimulationClock clock, SeededSimulationRandom random) = CreateDeterministicSources();
        DeterministicResourceSimulator simulator = new(clock, random, VirtualHardwareProfile.Default, maxSamplesPerProcess: 2);
        ProcessRecord process = CreateRunningProcess(1, ResourceProfile.None);

        for (int i = 0; i < 3; i++)
        {
            simulator.Tick([process]);
            clock.Advance();
        }

        IReadOnlyList<ProcessResourceSample> history = simulator.GetHistory(process.Pid);
        Assert.Equal(2, history.Count);
        Assert.Equal(1, history[0].Tick);
        Assert.Equal(2, history[1].Tick);
    }

    [Fact]
    public void System_history_aggregates_every_active_process_for_the_tick()
    {
        (ManualSimulationClock clock, SeededSimulationRandom random) = CreateDeterministicSources();
        DeterministicResourceSimulator simulator = new(clock, random, VirtualHardwareProfile.Default);
        ResourceProfile profile = new(0.1, 0.2, 0.1, 0.2, 0.1, 0.2, 0.1, 0.2);
        ProcessRecord first = CreateRunningProcess(1, profile);
        ProcessRecord second = CreateRunningProcess(2, profile);

        IReadOnlyList<ProcessResourceSample> samples = simulator.Tick([first, second]);
        SystemResourceSample system = Assert.Single(simulator.GetSystemHistory());

        Assert.Equal(samples.Sum(s => s.CpuUsage), system.TotalCpuUsage, precision: 10);
        Assert.Equal(samples.Sum(s => s.MemoryUsageBytes), system.TotalMemoryUsageBytes, precision: 6);
    }
}
