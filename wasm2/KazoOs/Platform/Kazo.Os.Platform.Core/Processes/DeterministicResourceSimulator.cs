using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>
/// Deterministic <see cref="IResourceSimulator"/> deriving per-tick usage from process state,
/// resource profile, explicit activity, virtual hardware capacity, and a persistent per-process
/// seeded random stream, per ADR 0012.
/// </summary>
public sealed class DeterministicResourceSimulator : IResourceSimulator
{
    private const double DefaultActivity = 0.5;
    private const double JitterRange = 0.1;

    private readonly ISimulationClock _clock;
    private readonly ISimulationRandom _random;
    private readonly VirtualHardwareProfile _hardware;
    private readonly int _maxSamplesPerProcess;
    private readonly int _maxSystemSamples;

    private readonly Lock _gate = new();
    private readonly Dictionary<ProcessId, ISimulationRandomStream> _streamsByPid = [];
    private readonly Dictionary<ProcessId, List<ProcessResourceSample>> _historyByPid = [];
    private readonly List<SystemResourceSample> _systemHistory = [];

    /// <summary>Initializes a deterministic resource simulator.</summary>
    /// <param name="clock">Simulation clock supplying the current tick/time for every sample.</param>
    /// <param name="random">Seeded random source supplying one persistent stream per process.</param>
    /// <param name="hardwareProfile">Virtual hardware capacity every tick clamps aggregate usage to.</param>
    /// <param name="maxSamplesPerProcess">Maximum retained samples per process; oldest is evicted first.</param>
    /// <param name="maxSystemSamples">Maximum retained system-wide samples; oldest is evicted first.</param>
    public DeterministicResourceSimulator(
        ISimulationClock clock,
        ISimulationRandom random,
        VirtualHardwareProfile hardwareProfile,
        int maxSamplesPerProcess = 120,
        int maxSystemSamples = 120)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSamplesPerProcess, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSystemSamples, 1);

        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _hardware = hardwareProfile ?? throw new ArgumentNullException(nameof(hardwareProfile));
        _maxSamplesPerProcess = maxSamplesPerProcess;
        _maxSystemSamples = maxSystemSamples;
    }

    /// <inheritdoc />
    public IReadOnlyList<ProcessResourceSample> Tick(
        IReadOnlyList<ProcessRecord> activeProcesses,
        IReadOnlyDictionary<ProcessId, WorkloadActivity>? activity = null)
    {
        ArgumentNullException.ThrowIfNull(activeProcesses);

        lock (_gate)
        {
            long tick = _clock.CurrentTick;
            DateTimeOffset now = _clock.UtcNow;

            List<(ProcessId Pid, double Cpu, double Memory, double StorageIo, double NetworkIo)> raw = [];
            foreach (ProcessRecord process in activeProcesses)
            {
                if (process.IsTerminal)
                {
                    continue;
                }

                double activityValue = activity is not null && activity.TryGetValue(process.Pid, out WorkloadActivity signal)
                    ? signal.Value
                    : DefaultActivity;

                double transitionFactor = process.State switch
                {
                    ProcessState.Running => 1.0,
                    ProcessState.Starting or ProcessState.Stopping => 0.5,
                    _ => 0.0
                };

                ISimulationRandomStream stream = GetStream(process.Pid);
                double jitter = 1.0 + ((stream.NextDouble() - 0.5) * JitterRange);

                double cpu = Math.Max(0, Band(process.ResourceProfile.BaselineCpuWeight, process.ResourceProfile.BurstCpuWeight, activityValue) * transitionFactor * jitter);
                double memory = Math.Max(0, Band(process.ResourceProfile.BaselineMemoryWeight, process.ResourceProfile.BurstMemoryWeight, activityValue) * transitionFactor);
                double storageIo = Math.Max(0, Band(process.ResourceProfile.BaselineStorageIoWeight, process.ResourceProfile.BurstStorageIoWeight, activityValue) * transitionFactor * jitter);
                double networkIo = Math.Max(0, Band(process.ResourceProfile.BaselineNetworkIoWeight, process.ResourceProfile.BurstNetworkIoWeight, activityValue) * transitionFactor * jitter);

                raw.Add((process.Pid, cpu, memory, storageIo, networkIo));
            }

            double cpuScale = ScaleFactor(raw.Sum(r => r.Cpu), _hardware.CpuCapacity);
            double memoryScale = ScaleFactor(raw.Sum(r => r.Memory), 1.0);
            double storageScale = ScaleFactor(raw.Sum(r => r.StorageIo), 1.0);
            double networkScale = ScaleFactor(raw.Sum(r => r.NetworkIo), 1.0);

            List<ProcessResourceSample> samples = [];
            double totalCpu = 0;
            double totalMemory = 0;
            double totalStorageIo = 0;
            double totalNetworkIo = 0;

            foreach ((ProcessId pid, double cpu, double memory, double storageIo, double networkIo) in raw)
            {
                double cpuUsage = cpu * cpuScale;
                double memoryUsageBytes = memory * memoryScale * _hardware.MemoryCapacityBytes;
                double storageIoBytes = storageIo * storageScale * _hardware.StorageIoCapacityBytesPerTick;
                double networkIoBytes = networkIo * networkScale * _hardware.NetworkIoCapacityBytesPerTick;

                ProcessResourceSample sample = new(pid, tick, now, cpuUsage, memoryUsageBytes, storageIoBytes, networkIoBytes);
                samples.Add(sample);
                AddToHistory(_historyByPid, pid, sample, _maxSamplesPerProcess);

                totalCpu += cpuUsage;
                totalMemory += memoryUsageBytes;
                totalStorageIo += storageIoBytes;
                totalNetworkIo += networkIoBytes;
            }

            _systemHistory.Add(new SystemResourceSample(tick, now, totalCpu, totalMemory, totalStorageIo, totalNetworkIo));
            if (_systemHistory.Count > _maxSystemSamples)
            {
                _systemHistory.RemoveAt(0);
            }

            return samples;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ProcessResourceSample> GetHistory(ProcessId pid)
    {
        lock (_gate)
        {
            return _historyByPid.TryGetValue(pid, out List<ProcessResourceSample>? history) ? [.. history] : [];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SystemResourceSample> GetSystemHistory()
    {
        lock (_gate)
        {
            return [.. _systemHistory];
        }
    }

    private ISimulationRandomStream GetStream(ProcessId pid)
    {
        if (!_streamsByPid.TryGetValue(pid, out ISimulationRandomStream? stream))
        {
            stream = _random.GetStream($"process:{pid.Value}:resources");
            _streamsByPid[pid] = stream;
        }

        return stream;
    }

    private static void AddToHistory(
        Dictionary<ProcessId, List<ProcessResourceSample>> historyByPid,
        ProcessId pid,
        ProcessResourceSample sample,
        int maxSamples)
    {
        if (!historyByPid.TryGetValue(pid, out List<ProcessResourceSample>? history))
        {
            history = [];
            historyByPid[pid] = history;
        }

        history.Add(sample);
        if (history.Count > maxSamples)
        {
            history.RemoveAt(0);
        }
    }

    private static double Band(double baseline, double burst, double activity) =>
        baseline + ((burst - baseline) * activity);

    private static double ScaleFactor(double totalRawWeight, double capacity) =>
        totalRawWeight > capacity && totalRawWeight > 0 ? capacity / totalRawWeight : 1.0;
}
