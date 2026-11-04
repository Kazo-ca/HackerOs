using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Processes.Accounting;

/// <summary>
/// CPU accountant of a host whose CPU measurement is off, at build or in <c>appsettings.json</c>
/// (ADR 0066, section 4): it never measures, so every reader falls back to the
/// <see cref="IResourceSimulator"/> and reports <see cref="ResourceMeasurementSource.Simulated"/>.
/// </summary>
public sealed class NullCpuTimeAccountant : ICpuTimeAccountant
{
    /// <summary>Gets the shared instance (the accountant is stateless).</summary>
    public static NullCpuTimeAccountant Instance { get; } = new();

    private NullCpuTimeAccountant()
    {
    }

    /// <inheritdoc />
    public bool IsMeasuring => false;

    /// <inheritdoc />
    public bool TryGetCpuUsage(ProcessId pid, out double coreEquivalents)
    {
        coreEquivalents = 0;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetProcessTimes(ProcessId pid, out ProcessCpuTimes times)
    {
        times = default;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetThreadTimes(out MeasuredThreadCpuTimes times)
    {
        times = default;
        return false;
    }
}

/// <summary>
/// Memory accountant of a host whose memory measurement is off (ADR 0066, section 4): every reader
/// falls back to the simulated value.
/// </summary>
public sealed class NullMemoryAccountant : IMemoryAccountant
{
    /// <summary>Gets the shared instance (the accountant is stateless).</summary>
    public static NullMemoryAccountant Instance { get; } = new();

    private NullMemoryAccountant()
    {
    }

    /// <inheritdoc />
    public bool IsMeasuring => false;

    /// <inheritdoc />
    public bool TryGetProcessMemory(ProcessRecord process, IReadOnlyList<ProcessRecord> active, out ProcessMemoryUsage usage)
    {
        usage = default;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetSystemMemory(IReadOnlyList<ProcessRecord> active, out SystemMemoryUsage usage)
    {
        usage = default;
        return false;
    }
}

/// <summary>
/// Kernel-memory ledger of a host whose memory measurement is off (ADR 0066, section 4): every charge
/// is <see cref="KernelMemoryCharge.None"/>, so the OS services that buffer for processes pay nothing.
/// </summary>
public sealed class NullKernelMemoryLedger : IKernelMemoryLedger
{
    /// <summary>Gets the shared instance (the ledger is stateless).</summary>
    public static NullKernelMemoryLedger Instance { get; } = new();

    private NullKernelMemoryLedger()
    {
    }

    /// <inheritdoc />
    public bool IsTracking => false;

    /// <inheritdoc />
    public KernelMemoryCharge Charge(ProcessId pid, KernelMemoryKind kind, long bytes = 0) => KernelMemoryCharge.None;
}
