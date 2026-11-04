using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Tests.Support;

/// <summary>
/// <see cref="IKernelMemoryLedger"/> double that keeps the current total per process and kind, so a
/// test can check what an OS service charges and that it releases it (ADR 0068).
/// </summary>
public sealed class RecordingKernelMemoryLedger : IKernelMemoryLedger
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(long Pid, KernelMemoryKind Kind), long> _bytes = [];

    /// <summary>Gets how many charges were opened.</summary>
    public int ChargesOpened { get; private set; }

    /// <inheritdoc />
    public bool IsTracking => true;

    /// <inheritdoc />
    public KernelMemoryCharge Charge(ProcessId pid, KernelMemoryKind kind, long bytes = 0)
    {
        lock (_gate)
        {
            ChargesOpened++;
        }

        return new KernelMemoryCharge(
            delta =>
            {
                lock (_gate)
                {
                    _bytes[(pid.Value, kind)] = _bytes.GetValueOrDefault((pid.Value, kind)) + delta;
                }
            },
            bytes);
    }

    /// <summary>Gets the bytes currently held for <paramref name="pid"/> as <paramref name="kind"/>.</summary>
    public long BytesOf(long pid, KernelMemoryKind kind)
    {
        lock (_gate)
        {
            return _bytes.GetValueOrDefault((pid, kind));
        }
    }
}
