using System.Collections.Concurrent;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.ResourceAccounting.Memory;

/// <summary>
/// The OS-resources layer of ADR 0068: what the OS holds for each process (write-stream buffers,
/// clipboard content, pipe buffers, sockets in flight), charged by the services that hold it. Exact:
/// every charge is resized by its owner as the buffer changes.
/// </summary>
public sealed class KernelMemoryLedger : IKernelMemoryLedger
{
    private static readonly int KindCount = Enum.GetValues<KernelMemoryKind>().Length;

    private readonly ConcurrentDictionary<long, long[]> _usage = new();

    /// <inheritdoc />
    public bool IsTracking => true;

    /// <inheritdoc />
    public KernelMemoryCharge Charge(ProcessId pid, KernelMemoryKind kind, long bytes = 0)
    {
        // The charge writes into the array it captured: a charge outliving its process (a clipboard
        // entry, say) keeps updating an array nobody reads any more, never a reused entry.
        long[] usage = _usage.GetOrAdd(pid.Value, static _ => new long[KindCount]);
        int slot = (int)kind;
        return new KernelMemoryCharge(delta => Interlocked.Add(ref usage[slot], delta), bytes);
    }

    /// <summary>Gets everything held for <paramref name="pid"/>.</summary>
    /// <param name="pid">The process.</param>
    public long GetBytes(ProcessId pid)
    {
        if (!_usage.TryGetValue(pid.Value, out long[]? usage))
        {
            return 0;
        }

        long total = 0;
        for (int i = 0; i < usage.Length; i++)
        {
            total += Interlocked.Read(ref usage[i]);
        }

        return total;
    }

    /// <summary>Gets what is held for <paramref name="pid"/> as <paramref name="kind"/>.</summary>
    /// <param name="pid">The process.</param>
    /// <param name="kind">The kind of object.</param>
    public long GetBytes(ProcessId pid, KernelMemoryKind kind) =>
        _usage.TryGetValue(pid.Value, out long[]? usage) ? Interlocked.Read(ref usage[(int)kind]) : 0;

    /// <summary>Forgets every process not in <paramref name="live"/>.</summary>
    /// <param name="live">The processes still alive.</param>
    public void Retain(IReadOnlySet<long> live)
    {
        ArgumentNullException.ThrowIfNull(live);
        foreach (long pid in _usage.Keys)
        {
            if (!live.Contains(pid))
            {
                _usage.TryRemove(pid, out _);
            }
        }
    }
}
