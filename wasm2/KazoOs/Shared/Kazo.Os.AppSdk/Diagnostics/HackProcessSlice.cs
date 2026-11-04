using System.Diagnostics;

namespace Kazo.Os.AppSdk.Diagnostics;

/// <summary>
/// The timestamp of the current time slice on this thread (ADR 0069, section 2): when a process last
/// got the thread. <see cref="HackProcess.CheckpointAsync"/> compares it with the 8 ms budget.
/// </summary>
/// <remarks>
/// <para>
/// Until the measured CPU time of ADR 0067 exists, the slice is approximated here: the OS's per-process
/// scheduler stamps a new slice each time it hands the thread to a process's continuation
/// (<see cref="Begin"/>), and a checkpoint reached by another process than the one that owns the stamp
/// starts a new slice. A slice can therefore look shorter than it really is (code that ran before the
/// first checkpoint is not counted) but never longer, so a checkpoint never yields more often than the
/// budget asks.
/// </para>
/// <para>
/// Thread-static, not <see cref="AsyncLocal{T}"/>: a slice belongs to the thread running the code, and
/// reading it must stay allocation-free and cheaper than the ambient context itself.
/// </para>
/// </remarks>
internal static class HackProcessSlice
{
    /// <summary>The slice budget: one frame at 60 fps.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(8);

    private static readonly long BudgetTicks = (long)(Budget.TotalSeconds * Stopwatch.Frequency);

    [ThreadStatic]
    private static long t_owner;

    [ThreadStatic]
    private static long t_start;

    /// <summary>Starts a new slice for <paramref name="processId"/> on this thread (a context switch).</summary>
    /// <param name="processId">PID of the process given the thread; <c>0</c> for code outside any process.</param>
    public static void Begin(long processId)
    {
        t_owner = processId;
        t_start = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Tells whether <paramref name="processId"/> has used up its slice; starts one when the current
    /// slice belongs to another process.
    /// </summary>
    /// <param name="processId">PID of the process reaching a checkpoint.</param>
    public static bool IsExhausted(long processId)
    {
        long now = Stopwatch.GetTimestamp();
        if (t_owner != processId || t_start == 0)
        {
            t_owner = processId;
            t_start = now;
            return false;
        }

        return now - t_start >= BudgetTicks;
    }
}
