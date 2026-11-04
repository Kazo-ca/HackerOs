namespace Kazo.Os.Simulation.Abstractions.Processes;

/// <summary>
/// Declares fixed virtual hardware capacity used to clamp aggregate simulated resource usage,
/// per ADR 0012. Upgrading the active profile changes future tick capacity/coefficients but
/// never rewrites process resource history.
/// </summary>
public sealed record VirtualHardwareProfile
{
    /// <summary>Initializes a validated virtual hardware profile.</summary>
    /// <param name="name">Non-empty display name for the profile, e.g. <c>baseline</c>.</param>
    /// <param name="cpuCapacity">Total simulated CPU capacity in core-equivalents; must be positive.</param>
    /// <param name="memoryCapacityBytes">Total simulated memory capacity in bytes; must be positive.</param>
    /// <param name="storageIoCapacityBytesPerTick">Total simulated storage I/O capacity per tick, in bytes; must be positive.</param>
    /// <param name="networkIoCapacityBytesPerTick">Total simulated network I/O capacity per tick, in bytes; must be positive.</param>
    /// <param name="storageCapacityBytes">
    /// Size of the virtual disk behind <c>/</c>, in bytes; must be positive. Defaults to
    /// <see cref="DefaultStorageCapacityBytes"/> (ADR 0057 section 7).
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A capacity is not positive.</exception>
    public VirtualHardwareProfile(
        string name,
        double cpuCapacity,
        double memoryCapacityBytes,
        double storageIoCapacityBytesPerTick,
        double networkIoCapacityBytesPerTick,
        long storageCapacityBytes = DefaultStorageCapacityBytes)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A profile name is required.", nameof(name));
        }

        ValidatePositive(cpuCapacity, nameof(cpuCapacity));
        ValidatePositive(memoryCapacityBytes, nameof(memoryCapacityBytes));
        ValidatePositive(storageIoCapacityBytesPerTick, nameof(storageIoCapacityBytesPerTick));
        ValidatePositive(networkIoCapacityBytesPerTick, nameof(networkIoCapacityBytesPerTick));
        ValidatePositive(storageCapacityBytes, nameof(storageCapacityBytes));

        Name = name;
        CpuCapacity = cpuCapacity;
        MemoryCapacityBytes = memoryCapacityBytes;
        StorageIoCapacityBytesPerTick = storageIoCapacityBytesPerTick;
        NetworkIoCapacityBytesPerTick = networkIoCapacityBytesPerTick;
        StorageCapacityBytes = storageCapacityBytes;
    }

    /// <summary>Default size of the virtual disk: 8 GiB (ADR 0057 section 7).</summary>
    public const long DefaultStorageCapacityBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>Gets the default single-core, 2 GiB baseline virtual hardware profile.</summary>
    public static VirtualHardwareProfile Default { get; } =
        new("baseline", cpuCapacity: 1.0, memoryCapacityBytes: 2L * 1024 * 1024 * 1024,
            storageIoCapacityBytesPerTick: 50_000_000, networkIoCapacityBytesPerTick: 10_000_000);

    /// <summary>Gets the display name for the profile.</summary>
    public string Name { get; }

    /// <summary>Gets the total simulated CPU capacity in core-equivalents.</summary>
    public double CpuCapacity { get; }

    /// <summary>Gets the total simulated memory capacity in bytes.</summary>
    public double MemoryCapacityBytes { get; }

    /// <summary>Gets the total simulated storage I/O capacity per tick, in bytes.</summary>
    public double StorageIoCapacityBytesPerTick { get; }

    /// <summary>Gets the total simulated network I/O capacity per tick, in bytes.</summary>
    public double NetworkIoCapacityBytesPerTick { get; }

    /// <summary>
    /// Gets the size of the virtual disk behind <c>/</c>, in bytes. <c>HackDriveInfo.TotalSize</c>
    /// reports it, lowered to the browser quota when the browser grants less (ADR 0057 section 7).
    /// </summary>
    public long StorageCapacityBytes { get; }

    private static void ValidatePositive(double value, string name)
    {
        if (double.IsNaN(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, "Capacity must be positive.");
        }
    }
}

/// <summary>
/// Represents an explicit, bounded workload/activity signal in <c>[0, 1]</c> that scales a
/// process's baseline-to-burst resource band for one tick, per ADR 0012.
/// </summary>
public readonly record struct WorkloadActivity
{
    private WorkloadActivity(double value) => Value = value;

    /// <summary>Gets the activity intensity in <c>[0, 1]</c>.</summary>
    public double Value { get; }

    /// <summary>Gets the idle (zero) activity signal.</summary>
    public static WorkloadActivity Idle { get; } = new(0);

    /// <summary>Gets the fully active (one) activity signal.</summary>
    public static WorkloadActivity Full { get; } = new(1);

    /// <summary>Creates an activity signal from an explicit intensity.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="intensity"/> is outside <c>[0, 1]</c>.</exception>
    public static WorkloadActivity FromIntensity(double intensity) => intensity is >= 0 and <= 1
        ? new WorkloadActivity(intensity)
        : throw new ArgumentOutOfRangeException(nameof(intensity), "Activity intensity must be within [0, 1].");
}

/// <summary>Represents one deterministic resource usage sample for a single process at a single tick.</summary>
public sealed record ProcessResourceSample
{
    /// <summary>Initializes a validated process resource sample.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tick"/> is negative or a usage value is negative.</exception>
    public ProcessResourceSample(
        ProcessId pid,
        long tick,
        DateTimeOffset atUtc,
        double cpuUsage,
        double memoryUsageBytes,
        double storageIoBytes,
        double networkIoBytes)
    {
        if (tick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), "Tick cannot be negative.");
        }

        ValidateNonNegative(cpuUsage, nameof(cpuUsage));
        ValidateNonNegative(memoryUsageBytes, nameof(memoryUsageBytes));
        ValidateNonNegative(storageIoBytes, nameof(storageIoBytes));
        ValidateNonNegative(networkIoBytes, nameof(networkIoBytes));

        Pid = pid;
        Tick = tick;
        AtUtc = atUtc;
        CpuUsage = cpuUsage;
        MemoryUsageBytes = memoryUsageBytes;
        StorageIoBytes = storageIoBytes;
        NetworkIoBytes = networkIoBytes;
    }

    /// <summary>Gets the process this sample describes.</summary>
    public ProcessId Pid { get; }

    /// <summary>Gets the simulation tick this sample was taken on.</summary>
    public long Tick { get; }

    /// <summary>Gets the UTC simulation time this sample was taken.</summary>
    public DateTimeOffset AtUtc { get; }

    /// <summary>Gets the simulated CPU usage in core-equivalents, clamped to hardware capacity.</summary>
    public double CpuUsage { get; }

    /// <summary>Gets the simulated memory usage in bytes, clamped to hardware capacity.</summary>
    public double MemoryUsageBytes { get; }

    /// <summary>Gets the simulated storage I/O for this tick, in bytes, clamped to hardware capacity.</summary>
    public double StorageIoBytes { get; }

    /// <summary>Gets the simulated network I/O for this tick, in bytes, clamped to hardware capacity.</summary>
    public double NetworkIoBytes { get; }

    private static void ValidateNonNegative(double value, string name)
    {
        if (double.IsNaN(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(name, "Usage cannot be negative.");
        }
    }
}

/// <summary>Represents one deterministic system-wide aggregate resource usage sample for a single tick.</summary>
public sealed record SystemResourceSample(
    long Tick,
    DateTimeOffset AtUtc,
    double TotalCpuUsage,
    double TotalMemoryUsageBytes,
    double TotalStorageIoBytes,
    double TotalNetworkIoBytes);

/// <summary>
/// Deterministically simulates per-tick CPU/memory/storage-I/O/network-I/O usage for active
/// processes, clamped to virtual hardware capacity, per ADR 0012.
/// </summary>
public interface IResourceSimulator
{
    /// <summary>
    /// Computes one deterministic resource sample for every non-terminal process in
    /// <paramref name="activeProcesses"/>, aggregates a system-wide sample, and records both in
    /// bounded history.
    /// </summary>
    /// <param name="activeProcesses">Currently active processes to sample; terminal processes are ignored and consume zero resources.</param>
    /// <param name="activity">Optional explicit workload/activity signal per process; unlisted processes default to a mid-level signal.</param>
    /// <returns>The per-process samples produced for this tick, in the order active processes were supplied.</returns>
    IReadOnlyList<ProcessResourceSample> Tick(
        IReadOnlyList<ProcessRecord> activeProcesses,
        IReadOnlyDictionary<ProcessId, WorkloadActivity>? activity = null);

    /// <summary>Gets bounded, most-recent-last resource history for one process, oldest first.</summary>
    IReadOnlyList<ProcessResourceSample> GetHistory(ProcessId pid);

    /// <summary>Gets bounded, most-recent-last system-wide aggregate resource history, oldest first.</summary>
    IReadOnlyList<SystemResourceSample> GetSystemHistory();
}
