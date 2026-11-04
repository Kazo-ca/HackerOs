using System.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.ResourceAccounting;

/// <summary>
/// The measured CPU time of ADR 0067: a kernel's per-task time accounting, driven by the common
/// context-switch hook (<see cref="ContextSwitchHook"/>, <see cref="ContextSwitchDispatcher"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>How time is charged.</b> At every switch the hook reports, the time elapsed since the previous
/// one on this thread is added to whoever was running: the process's <c>utime</c> while it ran its
/// own code, its <c>stime</c> while it was inside an OS gateway (its "kernel" sub-frame), and, outside
/// any process frame, either the session's compositor or idle. The clock is
/// <see cref="Stopwatch.GetTimestamp"/> (<c>performance.now()</c> in the browser).
/// </para>
/// <para>
/// <b>Compositor or idle.</b> The hook sees the thread leave a process, not the browser's event loop
/// going quiet. Time outside any frame is therefore split by how the process left: after a scope
/// the OS closed itself (an event handler, a render, a launch), the OS goes on running (Blazor
/// rendering, the window manager), so the gap is the compositor's, up to
/// <see cref="CompositorSliceBudget"/>; after the runtime restored an empty context (the end of a
/// continuation or a timer callback), the thread returns to the event loop and the gap is idle.
/// Like Xorg or <c>gnome-shell</c>, the compositor's time is system time in <c>/proc/stat</c>.
/// </para>
/// <para>
/// <b>Slices.</b> A slice is an uninterrupted run of one process on the thread, kernel mode included.
/// A slice over <see cref="LongTaskThreshold"/> (50 ms, the browser's long task) is counted; a slice
/// over the unresponsive threshold marks the process "Not responding" until it completes a slice
/// under it again. On the main thread this is only visible after the fact: nothing else runs while
/// the slice lasts (ADR 0067, section 4). Nothing is ever interrupted.
/// </para>
/// <para>
/// <b>Cost.</b> The hot path reads the clock, a few thread-static fields, and adds into a slot found
/// by PID in an array, with <see cref="Interlocked"/> adds and no lock. A process's slot is allocated
/// the first time it runs; the array grows (under a lock, off the hot path) only when PIDs outgrow it.
/// The per-thread state keeps the accounting right when tests or the server run code on several threads.
/// </para>
/// </remarks>
public sealed class MeasuredCpuTimeAccountant : ICpuTimeAccountant, IExecutionContextSwitchObserver, IExecutionModeSwitchObserver
{
    /// <summary>The browser's long-task threshold: a slice longer than this is counted (ADR 0067, section 4).</summary>
    public static readonly TimeSpan LongTaskThreshold = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Default part of an out-of-frame gap charged to the compositor after the OS closed a process
    /// scope itself: about one Blazor render pass.
    /// </summary>
    public static readonly TimeSpan DefaultCompositorSliceBudget = TimeSpan.FromMilliseconds(4);

    /// <summary>Window of the usage reported by <see cref="TryGetCpuUsage"/>: the last second.</summary>
    public static readonly TimeSpan UsageWindow = TimeSpan.FromSeconds(1);

    private const int InitialSlotCount = 256;

    [ThreadStatic]
    private static ThreadState? t_state;

    private readonly Func<long> _clock;
    private readonly long _frequency;
    private readonly long _unresponsiveTicks;
    private readonly long _longTaskTicks;
    private readonly long _compositorBudgetTicks;
    private readonly long _startedAt;
    private readonly Lock _growGate = new();
    private readonly Lock _usageGate = new();
    private ProcessSlot?[] _slots = new ProcessSlot?[InitialSlotCount];
    private long _compositorTicks;
    private long _outOfFrameTicks;

    /// <summary>Initializes an accountant on the real high-resolution clock.</summary>
    /// <param name="unresponsiveThreshold">A slice longer than this marks the process "Not responding" (ADR 0066 setting).</param>
    public MeasuredCpuTimeAccountant(TimeSpan unresponsiveThreshold)
        : this(unresponsiveThreshold, Stopwatch.GetTimestamp, Stopwatch.Frequency)
    {
    }

    /// <summary>Initializes an accountant on an explicit clock (tests drive time by hand).</summary>
    /// <param name="unresponsiveThreshold">A slice longer than this marks the process "Not responding".</param>
    /// <param name="clock">Returns the current timestamp, in <paramref name="frequency"/> ticks per second.</param>
    /// <param name="frequency">Ticks per second of <paramref name="clock"/>.</param>
    /// <param name="compositorSliceBudget">Compositor share of a gap after an OS-closed scope; <see cref="DefaultCompositorSliceBudget"/> when omitted.</param>
    public MeasuredCpuTimeAccountant(TimeSpan unresponsiveThreshold, Func<long> clock, long frequency, TimeSpan? compositorSliceBudget = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(unresponsiveThreshold, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(frequency, 0);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _frequency = frequency;
        UnresponsiveThreshold = unresponsiveThreshold;
        CompositorSliceBudget = compositorSliceBudget ?? DefaultCompositorSliceBudget;
        _unresponsiveTicks = ToTicks(unresponsiveThreshold);
        _longTaskTicks = ToTicks(LongTaskThreshold);
        _compositorBudgetTicks = ToTicks(CompositorSliceBudget);
        _startedAt = clock();
    }

    /// <summary>Gets the slice length above which a process is "Not responding".</summary>
    public TimeSpan UnresponsiveThreshold { get; }

    /// <summary>Gets the most of one out-of-frame gap charged to the compositor (see the remarks).</summary>
    public TimeSpan CompositorSliceBudget { get; }

    /// <inheritdoc />
    public bool IsMeasuring => true;

    /// <inheritdoc />
    public void OnContextSwitch(in ExecutionContextSwitch contextSwitch)
    {
        long now = _clock();
        ThreadState state = StateFor(now);
        long elapsed = Math.Max(0, now - state.SegmentStart);

        if (contextSwitch.From is { } from)
        {
            // The process leaving the thread: charge its last segment and close its slice.
            ProcessSlot slot = SlotFor(from.Value, now);
            Interlocked.Add(ref contextSwitch.FromKernel ? ref slot.SystemTicks : ref slot.UserTicks, elapsed);
            EndSlice(slot, now - state.SliceStart);
        }
        else
        {
            ChargeOutOfFrame(state, elapsed);
        }

        if (contextSwitch.To is { } to)
        {
            // Allocates the slot the first time the process runs, so its usage window starts now.
            SlotFor(to.Value, now);
        }

        state.SegmentStart = now;
        state.SliceStart = now;
        // Leaving a process for no process: the OS goes on (compositor) only when it closed the scope
        // itself; a runtime restore means the continuation ended and the thread goes back to the loop.
        state.GapIsCompositor = contextSwitch.To is null && contextSwitch.From is not null && !contextSwitch.ThreadContextChanged;
    }

    /// <inheritdoc />
    public void OnModeSwitch(ProcessId process, bool toKernel, bool threadContextChanged)
    {
        long now = _clock();
        ThreadState state = StateFor(now);
        long elapsed = Math.Max(0, now - state.SegmentStart);
        ProcessSlot slot = SlotFor(process.Value, now);
        // The segment that just ended was in the other mode. The slice goes on: same process.
        Interlocked.Add(ref toKernel ? ref slot.UserTicks : ref slot.SystemTicks, elapsed);
        state.SegmentStart = now;
    }

    /// <inheritdoc />
    public bool TryGetCpuUsage(ProcessId pid, out double coreEquivalents)
    {
        coreEquivalents = 0;
        if (TryGetSlot(pid.Value) is not { } slot)
        {
            return false;
        }

        long now = _clock();
        lock (_usageGate)
        {
            long total = Volatile.Read(ref slot.UserTicks) + Volatile.Read(ref slot.SystemTicks);
            long window = now - slot.UsageSampledAt;
            if (!slot.UsageSampled || window >= ToTicks(UsageWindow))
            {
                long since = slot.UsageSampled ? slot.UsageSampledAt : slot.FirstSeenAt;
                long span = now - since;
                slot.LastUsage = span <= 0 ? 0 : Math.Clamp((double)(total - slot.UsageSampledTotal) / span, 0, 1);
                slot.UsageSampled = true;
                slot.UsageSampledAt = now;
                slot.UsageSampledTotal = total;
            }

            coreEquivalents = slot.LastUsage;
        }

        return true;
    }

    /// <inheritdoc />
    public bool TryGetProcessTimes(ProcessId pid, out ProcessCpuTimes times)
    {
        if (TryGetSlot(pid.Value) is not { } slot)
        {
            times = default;
            return false;
        }

        times = new ProcessCpuTimes(
            ToTimeSpan(Volatile.Read(ref slot.UserTicks)),
            ToTimeSpan(Volatile.Read(ref slot.SystemTicks)),
            Volatile.Read(ref slot.LongTasks),
            ToTimeSpan(Volatile.Read(ref slot.LongestSliceTicks)),
            Volatile.Read(ref slot.Unresponsive) == 0,
            Volatile.Read(ref slot.Slices));
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Processes that never ran are not measured (<see cref="TryGetProcessTimes"/> is false): they
    /// have no time yet, and the processes that run no code stay simulated (ADR 0066, section 4).
    /// </remarks>
    public bool TryGetThreadTimes(out MeasuredThreadCpuTimes times)
    {
        long user = 0;
        long system = 0;
        ProcessSlot?[] slots = Volatile.Read(ref _slots);
        foreach (ProcessSlot? slot in slots)
        {
            if (slot is not null)
            {
                user += Volatile.Read(ref slot.UserTicks);
                system += Volatile.Read(ref slot.SystemTicks);
            }
        }

        long compositor = Volatile.Read(ref _compositorTicks);
        long elapsed = Math.Max(0, _clock() - _startedAt);
        // Idle is the rest of the wall time: it absorbs the segment still running on this thread,
        // and never goes negative when several threads ran app code at once (tests, the server).
        long idle = Math.Max(0, elapsed - user - system - compositor);
        times = new MeasuredThreadCpuTimes(
            ToTimeSpan(user),
            ToTimeSpan(system + compositor),
            ToTimeSpan(compositor),
            ToTimeSpan(idle),
            ToTimeSpan(elapsed));
        return true;
    }

    /// <summary>Gets the wall time spent outside any process frame (compositor plus idle) so far, for diagnostics and tests.</summary>
    public TimeSpan OutOfFrameTime => ToTimeSpan(Volatile.Read(ref _outOfFrameTicks));

    /// <summary>
    /// Gets this thread's state for this accountant; a thread first seen (or last used by another
    /// accountant, as in tests) starts its first segment now.
    /// </summary>
    private ThreadState StateFor(long now)
    {
        ThreadState? state = t_state;
        if (state is null || !ReferenceEquals(state.Owner, this))
        {
            state = new ThreadState(this, now);
            t_state = state;
        }

        return state;
    }

    private void ChargeOutOfFrame(ThreadState state, long elapsed)
    {
        Interlocked.Add(ref _outOfFrameTicks, elapsed);
        if (state.GapIsCompositor)
        {
            Interlocked.Add(ref _compositorTicks, Math.Min(elapsed, _compositorBudgetTicks));
        }
    }

    private void EndSlice(ProcessSlot slot, long slice)
    {
        Interlocked.Increment(ref slot.Slices);
        if (slice > _longTaskTicks)
        {
            Interlocked.Increment(ref slot.LongTasks);
        }

        long longest = Volatile.Read(ref slot.LongestSliceTicks);
        while (slice > longest)
        {
            long seen = Interlocked.CompareExchange(ref slot.LongestSliceTicks, slice, longest);
            if (seen == longest)
            {
                break;
            }

            longest = seen;
        }

        // "Not responding" sticks until the process answers an event in less than the threshold.
        Volatile.Write(ref slot.Unresponsive, slice > _unresponsiveTicks ? 1 : 0);
    }

    private ProcessSlot? TryGetSlot(long pid)
    {
        ProcessSlot?[] slots = Volatile.Read(ref _slots);
        return pid > 0 && pid < slots.Length ? slots[pid] : null;
    }

    /// <summary>Gets (allocating once per process) the slot of <paramref name="pid"/>.</summary>
    private ProcessSlot SlotFor(long pid, long now)
    {
        ProcessSlot?[] slots = Volatile.Read(ref _slots);
        if (pid >= 0 && pid < slots.Length && slots[pid] is { } existing)
        {
            return existing;
        }

        return CreateSlot(pid, now);
    }

    private ProcessSlot CreateSlot(long pid, long now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pid);
        lock (_growGate)
        {
            ProcessSlot?[] slots = _slots;
            if (pid >= slots.Length)
            {
                long size = slots.Length;
                while (size <= pid)
                {
                    size *= 2;
                }

                ProcessSlot?[] grown = new ProcessSlot?[size];
                Array.Copy(slots, grown, slots.Length);
                slots = grown;
            }

            ProcessSlot slot = slots[pid] ??= new ProcessSlot(now);
            Volatile.Write(ref _slots, slots);
            return slot;
        }
    }

    private long ToTicks(TimeSpan span) => (long)(span.TotalSeconds * _frequency);

    private TimeSpan ToTimeSpan(long ticks) => TimeSpan.FromSeconds((double)ticks / _frequency);

    /// <summary>The counters of one process. Fields, so the hot path adds into them by reference.</summary>
    private sealed class ProcessSlot(long firstSeenAt)
    {
        public long UserTicks;
        public long SystemTicks;
        public long LongTasks;
        public long Slices;
        public long LongestSliceTicks;
        public int Unresponsive;

        public readonly long FirstSeenAt = firstSeenAt;

        // Usage over the last window, guarded by _usageGate (readers only, never the hot path).
        public bool UsageSampled;
        public long UsageSampledAt;
        public long UsageSampledTotal;
        public double LastUsage;
    }

    /// <summary>What runs on one thread since its last switch.</summary>
    private sealed class ThreadState(MeasuredCpuTimeAccountant owner, long now)
    {
        public readonly MeasuredCpuTimeAccountant Owner = owner;
        public long SegmentStart = now;
        public long SliceStart = now;
        public bool GapIsCompositor;
    }
}
