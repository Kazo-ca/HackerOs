using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>
/// In-memory <see cref="IProcessFileDescriptorTable"/> (ADR 0125, <c>RLIMIT_NOFILE</c>): the open
/// descriptors behind <c>/proc/&lt;pid&gt;/fd</c>. Entries are created and dropped by
/// <see cref="InMemoryProcessManager"/>; a process that ends closes every descriptor it still held,
/// like the kernel at <c>exit</c>.
/// </summary>
/// <remarks>
/// Each process starts with descriptors 0, 1 and 2 (its standard streams), which count toward the
/// limit as on Linux: with <c>ulimit -n 3</c> no file can be opened. An open takes the lowest free
/// number and fails when that number is not below the process's soft <c>nofile</c> limit.
/// </remarks>
public sealed class InMemoryProcessFileDescriptorTable : IProcessFileDescriptorTable
{
    /// <summary>Number of standard streams every process starts with.</summary>
    public const int StandardStreamCount = 3;

    private readonly Lock _gate = new();
    private readonly Dictionary<ProcessId, Entry> _entries = [];
    private readonly Func<ProcessId, long> _noFileLimit;

    /// <summary>Initializes the table.</summary>
    /// <param name="noFileLimit">Reads the soft <c>nofile</c> limit of a process; unlimited when omitted.</param>
    public InMemoryProcessFileDescriptorTable(Func<ProcessId, long>? noFileLimit = null)
    {
        _noFileLimit = noFileLimit ?? (static _ => ResourceLimit.Unlimited);
    }

    /// <inheritdoc />
    public bool TryOpen(ProcessId pid, string target, out int descriptor)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        long limit = _noFileLimit(pid);
        lock (_gate)
        {
            if (!_entries.TryGetValue(pid, out Entry? entry))
            {
                descriptor = -1;
                return true;
            }

            int number = StandardStreamCount;
            while (entry.Open.ContainsKey(number))
            {
                number++;
            }

            if (number >= limit)
            {
                descriptor = -1;
                return false;
            }

            entry.Open[number] = target;
            descriptor = number;
            return true;
        }
    }

    /// <inheritdoc />
    public void Close(ProcessId pid, int descriptor)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(pid, out Entry? entry))
            {
                entry.Open.Remove(descriptor);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ProcessFileDescriptor> GetOpen(ProcessId pid)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(pid, out Entry? entry))
            {
                return [];
            }

            List<ProcessFileDescriptor> open = new(StandardStreamCount + entry.Open.Count);
            for (int number = 0; number < StandardStreamCount; number++)
            {
                open.Add(new ProcessFileDescriptor(number, entry.StandardTarget));
            }

            open.AddRange(entry.Open.OrderBy(static pair => pair.Key).Select(static pair => new ProcessFileDescriptor(pair.Key, pair.Value)));
            return open;
        }
    }

    /// <summary>Creates the descriptor table of a new process.</summary>
    /// <param name="pid">The process.</param>
    /// <param name="standardTarget">What its standard streams designate: <c>/dev/tty</c> for a command, <c>/dev/null</c> otherwise.</param>
    internal void Create(ProcessId pid, string standardTarget)
    {
        lock (_gate)
        {
            _entries[pid] = new Entry(standardTarget);
        }
    }

    /// <summary>Closes every descriptor of a process that ended.</summary>
    internal void Remove(ProcessId pid)
    {
        lock (_gate)
        {
            _entries.Remove(pid);
        }
    }

    private sealed class Entry(string standardTarget)
    {
        public string StandardTarget { get; } = standardTarget;

        public SortedDictionary<int, string> Open { get; } = [];
    }
}
