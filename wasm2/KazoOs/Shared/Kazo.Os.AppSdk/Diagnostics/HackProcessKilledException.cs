using System.Globalization;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.AppSdk.Diagnostics;

/// <summary>
/// Thrown by every OS gateway called on behalf of a process the OS has killed (ADR 0069,
/// section 3): <c>kill -9</c>, "Force kill", or a <c>SIGTERM</c> whose grace period ran out. The
/// process no longer exists; the code still running for it is a leftover, like a thread of a Linux
/// process after <c>SIGKILL</c>, and is cut off from the OS.
/// </summary>
/// <remarks>
/// It derives from <see cref="OperationCanceledException"/> so the usual cancellation paths unwind it
/// quietly. App code has nothing to do with it: there is no one left to report to.
/// </remarks>
public sealed class HackProcessKilledException : OperationCanceledException
{
    /// <summary>Initializes the exception for one killed process.</summary>
    /// <param name="processId">PID of the killed process.</param>
    public HackProcessKilledException(long processId)
        : base($"Process {processId.ToString(CultureInfo.InvariantCulture)} was killed; the OS refuses its calls.")
    {
        ProcessId = processId;
        Reason = ProcessExitReason.Killed;
    }

    /// <summary>Initializes the exception for one process killed for a given reason (ADR 0128, section 4).</summary>
    /// <param name="processId">PID of the killed process.</param>
    /// <param name="reason">
    /// Why the OS killed it: <see cref="ProcessExitReason.Killed"/> for <c>kill -9</c> or the task
    /// manager, or the limit it crossed (ADR 0125, ADR 0126).
    /// </param>
    public HackProcessKilledException(long processId, ProcessExitReason reason)
        : this(processId)
    {
        Reason = reason;
    }

    /// <summary>Gets the PID of the killed process.</summary>
    public long ProcessId { get; }

    /// <summary>Gets why the OS killed the process; <see cref="ProcessExitReason.Killed"/> unless a limit is named.</summary>
    public ProcessExitReason Reason { get; }
}
