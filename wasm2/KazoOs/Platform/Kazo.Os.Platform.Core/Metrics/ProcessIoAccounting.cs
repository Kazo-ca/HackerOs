using Kazo.Os.Simulation.Abstractions.Metrics;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Metrics;

/// <summary>
/// In-memory <see cref="IProcessIoAccounting"/> (ADR 0057 section 3): one row of seven counters per
/// process that has done I/O, updated under a lock. A row is a handful of integers and is dropped by
/// <see cref="Retain"/> once its process is gone, so the table stays as small as the process list.
/// </summary>
public sealed class ProcessIoAccounting : IProcessIoAccounting
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ProcessId, ProcessIoCounters> _counters = [];

    /// <inheritdoc />
    public void RecordRead(ProcessId pid, long bytes, bool fromStorage)
    {
        long amount = Math.Max(0, bytes);
        lock (_gate)
        {
            ProcessIoCounters current = _counters.GetValueOrDefault(pid);
            _counters[pid] = current with
            {
                ReadChars = current.ReadChars + amount,
                ReadSyscalls = current.ReadSyscalls + 1,
                ReadBytes = fromStorage ? current.ReadBytes + amount : current.ReadBytes
            };
        }
    }

    /// <inheritdoc />
    public void RecordWrite(ProcessId pid, long bytes, bool toStorage)
    {
        long amount = Math.Max(0, bytes);
        lock (_gate)
        {
            ProcessIoCounters current = _counters.GetValueOrDefault(pid);
            _counters[pid] = current with
            {
                WrittenChars = current.WrittenChars + amount,
                WriteSyscalls = current.WriteSyscalls + 1,
                WriteBytes = toStorage ? current.WriteBytes + amount : current.WriteBytes
            };
        }
    }

    /// <inheritdoc />
    public ProcessIoCounters Get(ProcessId pid)
    {
        lock (_gate)
        {
            return _counters.GetValueOrDefault(pid);
        }
    }

    /// <inheritdoc />
    public void Retain(IReadOnlyCollection<ProcessId> livePids)
    {
        ArgumentNullException.ThrowIfNull(livePids);
        HashSet<ProcessId> live = [.. livePids];
        lock (_gate)
        {
            foreach (ProcessId pid in _counters.Keys.Where(pid => !live.Contains(pid)).ToArray())
            {
                _counters.Remove(pid);
            }
        }
    }
}
