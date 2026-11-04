namespace Kazo.Os.Simulation.Abstractions.Processes;

/// <summary>
/// Where a per-process CPU or memory figure comes from (ADR 0066, section 4). Shown in
/// <c>/proc/&lt;pid&gt;/status</c> as <c>CpuSource:</c> and <c>MemSource:</c>.
/// </summary>
public enum ResourceMeasurementSource
{
    /// <summary>Drawn from the app's declared <see cref="ResourceProfile"/> by the <see cref="IResourceSimulator"/> (ADR 0012).</summary>
    Simulated,

    /// <summary>Really measured by the OS at context switches (ADR 0067, 0068).</summary>
    Measured
}

/// <summary>
/// Measured CPU time per process (ADR 0066, 0067). Platform Core only knows this contract and its
/// null implementation; the measuring implementation ships in
/// <c>Kazo.Os.Platform.ResourceAccounting</c>, which a host may leave out of its binary.
/// </summary>
/// <remarks>
/// When <see cref="TryGetCpuUsage"/> returns <see langword="false"/>, the reader falls back to the
/// <see cref="IResourceSimulator"/> exactly as before phase 5, and reports
/// <see cref="ResourceMeasurementSource.Simulated"/>.
/// </remarks>
public interface ICpuTimeAccountant
{
    /// <summary>Gets whether this accountant measures anything at all (<see langword="false"/> for the null accountant).</summary>
    bool IsMeasuring { get; }

    /// <summary>Gets the measured CPU usage of <paramref name="pid"/> over the last second.</summary>
    /// <param name="pid">The process to read.</param>
    /// <param name="coreEquivalents">The usage in core-equivalents (1.0 = one virtual core fully busy).</param>
    /// <returns><see langword="true"/> when the process is measured; otherwise the simulator applies.</returns>
    bool TryGetCpuUsage(ProcessId pid, out double coreEquivalents);

    /// <summary>
    /// Gets the cumulative CPU times of <paramref name="pid"/> since it first ran (ADR 0067, section 3):
    /// the <c>utime</c> and <c>stime</c> of <c>/proc/&lt;pid&gt;/stat</c>, its long tasks and whether it responds.
    /// </summary>
    /// <param name="pid">The process to read.</param>
    /// <param name="times">The measured times.</param>
    /// <returns><see langword="true"/> when the process is measured; otherwise <c>/proc</c> renders simulated or zero values.</returns>
    bool TryGetProcessTimes(ProcessId pid, out ProcessCpuTimes times);

    /// <summary>
    /// Gets the measured time of the thread that runs app code since the accountant started
    /// (ADR 0067, section 2): the sum of every process's <c>utime</c>, of every <c>stime</c> plus the
    /// compositor, and the idle rest. Feeds the real <c>/proc/stat</c>.
    /// </summary>
    /// <param name="times">The measured thread times.</param>
    /// <returns><see langword="true"/> when measuring; otherwise <c>/proc/stat</c> stays simulated.</returns>
    bool TryGetThreadTimes(out MeasuredThreadCpuTimes times);
}

/// <summary>
/// The measured CPU times of one process (ADR 0067), shaped like what Linux keeps in its
/// <c>task_struct</c> and what <c>System.Diagnostics.Process</c> exposes.
/// </summary>
/// <param name="UserTime"><c>utime</c>: time spent in the app's own code.</param>
/// <param name="SystemTime"><c>stime</c>: time spent in the OS gateways the app called (files, network, clipboard).</param>
/// <param name="LongTaskCount">Slices longer than 50 ms, the browser's long-task threshold (ADR 0067, section 4).</param>
/// <param name="LongestSlice">The longest uninterrupted slice the process ever held the thread.</param>
/// <param name="IsResponding">
/// <see langword="false"/> once a single slice lasted longer than the unresponsive threshold, until
/// the process completes a slice under it again.
/// </param>
/// <param name="VoluntaryContextSwitches">
/// Slices completed: each time the process gave the thread back (<c>voluntary_ctxt_switches</c>).
/// Nothing is ever preempted on the main thread, so there is no involuntary counterpart.
/// </param>
public readonly record struct ProcessCpuTimes(
    TimeSpan UserTime,
    TimeSpan SystemTime,
    long LongTaskCount,
    TimeSpan LongestSlice,
    bool IsResponding,
    long VoluntaryContextSwitches = 0)
{
    /// <summary>Gets <see cref="UserTime"/> plus <see cref="SystemTime"/>.</summary>
    public TimeSpan TotalTime => UserTime + SystemTime;
}

/// <summary>
/// The measured split of the thread that runs app code (one virtual core while every process runs on
/// the browser's main thread, ADR 0067, section 3).
/// </summary>
/// <param name="User">Sum of every process's <c>utime</c>.</param>
/// <param name="System">Sum of every process's <c>stime</c> plus <paramref name="Compositor"/>.</param>
/// <param name="Compositor">Time outside any process frame charged to the session's GUI (Blazor rendering, OS loop).</param>
/// <param name="Idle">The rest of the wall time since the accountant started.</param>
/// <param name="Elapsed">Wall time since the accountant started.</param>
public readonly record struct MeasuredThreadCpuTimes(
    TimeSpan User,
    TimeSpan System,
    TimeSpan Compositor,
    TimeSpan Idle,
    TimeSpan Elapsed);

/// <summary>
/// Measured memory per process (ADR 0066, 0068). Same model as <see cref="ICpuTimeAccountant"/>:
/// a <see langword="false"/> answer means the simulated value applies.
/// </summary>
/// <remarks>
/// The reader passes the live process table with every call: the accountant uses it to split the
/// real managed heap between the processes still alive and to forget the ones that are gone, like
/// the kernel freeing a reaped task's pages. It never resolves the process table itself.
/// </remarks>
public interface IMemoryAccountant
{
    /// <summary>Gets whether this accountant measures anything at all (<see langword="false"/> for the null accountant).</summary>
    bool IsMeasuring { get; }

    /// <summary>Gets the measured memory of one process, layer by layer (ADR 0068, section 1).</summary>
    /// <param name="process">The process to read.</param>
    /// <param name="active">Every live process, among which the resident heap is split.</param>
    /// <param name="usage">The layers of <paramref name="process"/>.</param>
    /// <returns><see langword="true"/> when the process is measured; otherwise the simulator applies (a simulated process that runs no code).</returns>
    bool TryGetProcessMemory(ProcessRecord process, IReadOnlyList<ProcessRecord> active, out ProcessMemoryUsage usage);

    /// <summary>Gets the measured memory of the whole machine, behind <c>/proc/meminfo</c>.</summary>
    /// <param name="active">Every live process.</param>
    /// <param name="usage">The machine-wide totals, shared images counted once.</param>
    /// <returns><see langword="true"/> when memory is measured; otherwise the simulator applies.</returns>
    bool TryGetSystemMemory(IReadOnlyList<ProcessRecord> active, out SystemMemoryUsage usage);
}

/// <summary>
/// One change of the process running on the current thread, seen by the ambient-frame hook
/// (ADR 0067, section 1). <see langword="null"/> means "outside any process frame" (OS code,
/// Blazor rendering, idle).
/// </summary>
/// <param name="From">The process that stops running here, if any.</param>
/// <param name="To">The process that starts running here, if any.</param>
/// <param name="ThreadContextChanged">
/// <see langword="true"/> when the runtime restored an <see cref="ExecutionContext"/> (start or end
/// of a continuation, a timer callback, ...) rather than code assigning the frame explicitly.
/// </param>
/// <param name="FromKernel">
/// <see langword="true"/> when <paramref name="From"/> was inside an OS gateway (its "kernel" sub-frame,
/// ADR 0067, section 2) when it stopped running here.
/// </param>
/// <param name="ToKernel"><see langword="true"/> when <paramref name="To"/> resumes inside an OS gateway.</param>
public readonly record struct ExecutionContextSwitch(
    ProcessId? From,
    ProcessId? To,
    bool ThreadContextChanged,
    bool FromKernel = false,
    bool ToKernel = false);

/// <summary>
/// Receives every context switch between processes on the thread that runs app code: the common
/// hook shared by the CPU (ADR 0067) and memory (ADR 0068) measurements.
/// </summary>
/// <remarks>
/// <para>
/// The callback runs <b>inside</b> the runtime's execution-context restore, on the hot path of
/// every <c>await</c> continuation. Implementations must be allocation-free, lock-free on the main
/// thread, and must never throw (a throwing observer is skipped by the dispatcher, but the
/// measurement it was making is lost).
/// </para>
/// <para>
/// Switches where the process does not change (nested frames of the same process, or OS code
/// resuming OS code) are not reported.
/// </para>
/// </remarks>
public interface IExecutionContextSwitchObserver
{
    /// <summary>Called when the running process changes on the current thread.</summary>
    /// <param name="contextSwitch">The process left, the process entered, and how.</param>
    void OnContextSwitch(in ExecutionContextSwitch contextSwitch);
}

/// <summary>
/// Optional companion of <see cref="IExecutionContextSwitchObserver"/>: receives the changes of
/// <b>mode</b> of the running process, when it enters or leaves an OS gateway's "kernel" sub-frame
/// (ADR 0067, section 2). The process does not change, so <see cref="IExecutionContextSwitchObserver"/>
/// is not called for these.
/// </summary>
/// <remarks>Same hot-path rules as <see cref="IExecutionContextSwitchObserver"/>: no allocation, no lock, never throw.</remarks>
public interface IExecutionModeSwitchObserver
{
    /// <summary>Called when the running process enters or leaves kernel mode on the current thread.</summary>
    /// <param name="process">The running process.</param>
    /// <param name="toKernel"><see langword="true"/> when it enters an OS gateway, <see langword="false"/> when it returns to its own code.</param>
    /// <param name="threadContextChanged"><see langword="true"/> when the runtime restored an <see cref="ExecutionContext"/>.</param>
    void OnModeSwitch(ProcessId process, bool toKernel, bool threadContextChanged);
}
