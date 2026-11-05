using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.WorkerHost.Host;

/// <summary>
/// The run queue of commands waiting for a worker (ADR 0127 §1, ADR 0071): the lowest niceness is
/// served first, then the earliest arrival, like the order of Linux's CFS between runnable tasks of
/// different weight, reduced to a queue since a worker has no time slices to share. Niceness only
/// orders this queue (and the background jobs of ADR 0128): it never slows a running process.
/// </summary>
/// <remarks>Not thread-safe: <see cref="WorkerPool"/> uses it under its own lock.</remarks>
internal sealed class WorkerRunQueue
{
    private readonly List<Entry> _entries = [];
    private long _nextSequence;

    /// <summary>Gets the number of waiting commands.</summary>
    public int Count => _entries.Count;

    /// <summary>Queues a command of niceness <paramref name="nice"/> (clamped to <c>-20</c>..<c>19</c>).</summary>
    /// <param name="nice">Niceness of the command's process.</param>
    /// <returns>The command's place, to pass back to <see cref="IsHead"/> and <see cref="Remove"/>.</returns>
    public Entry Enqueue(int nice)
    {
        Entry entry = new(ProcessNice.Clamp(nice), ++_nextSequence);
        _entries.Add(entry);
        return entry;
    }

    /// <summary>Gets whether <paramref name="entry"/> is served next: lowest niceness, then arrival.</summary>
    /// <param name="entry">A place returned by <see cref="Enqueue"/>.</param>
    public bool IsHead(Entry entry) => ReferenceEquals(Head(), entry);

    /// <summary>Leaves the queue (placed, cancelled or failed).</summary>
    /// <param name="entry">A place returned by <see cref="Enqueue"/>.</param>
    public void Remove(Entry entry) => _entries.Remove(entry);

    /// <summary>
    /// Gets whether a command may take the always-ready worker (ADR 0071 §3). A positive niceness
    /// leaves it alone while another worker is about to be free, so the next interactive command
    /// still starts at once (ADR 0127 §1).
    /// </summary>
    /// <param name="nice">Niceness of the command.</param>
    /// <param name="anotherWorkerIsWarming">Whether a pool or overflow worker is still starting.</param>
    public static bool MayTakeReadyWorker(int nice, bool anotherWorkerIsWarming) => nice <= 0 || !anotherWorkerIsWarming;

    private Entry? Head()
    {
        Entry? head = null;
        foreach (Entry candidate in _entries)
        {
            if (head is null || candidate.Nice < head.Nice || (candidate.Nice == head.Nice && candidate.Sequence < head.Sequence))
            {
                head = candidate;
            }
        }

        return head;
    }

    /// <summary>One waiting command.</summary>
    /// <param name="Nice">Its niceness.</param>
    /// <param name="Sequence">Its arrival order.</param>
    internal sealed record Entry(int Nice, long Sequence);
}
