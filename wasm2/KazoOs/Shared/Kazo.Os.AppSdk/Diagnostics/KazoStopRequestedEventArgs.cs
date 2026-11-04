using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.AppSdk.Diagnostics;

/// <summary>
/// The OS asks the process to stop (ADR 0069, section 1): the <c>SIGTERM</c> it receives before
/// its <see cref="IAppExecutionContext.CancellationToken"/> is cancelled. Raised once per process,
/// on <see cref="IAppExecutionContext.StopRequested"/> and <see cref="KazoCurrentProcess.StopRequested"/>.
/// </summary>
/// <remarks>
/// The handler runs synchronously, inside the process's own ambient context, while the process is
/// already <c>Stopping</c> and before its token is cancelled: it is the place to stop accepting work,
/// flush a buffer or tell a worker loop to finish. A long cleanup belongs to the code that observes
/// the token, not to the handler. Past <see cref="Deadline"/>, the OS kills the process: its pending
/// continuations are dropped and its gateways throw <see cref="KazoProcessKilledException"/>.
/// </remarks>
public sealed class KazoStopRequestedEventArgs : EventArgs
{
    /// <summary>Initializes the event data.</summary>
    /// <param name="reason">Why the process is asked to stop.</param>
    /// <param name="requestedAt">Simulated time of the request.</param>
    /// <param name="deadline">Simulated time at which the OS forces the process.</param>
    public KazoStopRequestedEventArgs(ProcessExitReason reason, DateTimeOffset requestedAt, DateTimeOffset deadline)
    {
        Reason = reason;
        RequestedAt = requestedAt;
        Deadline = deadline;
    }

    /// <summary>
    /// Gets why the process is asked to stop: <see cref="ProcessExitReason.CloseRequested"/> (its window
    /// is closing), <see cref="ProcessExitReason.Terminated"/> (<c>kill</c>, "End task"),
    /// <see cref="ProcessExitReason.Logout"/> or <see cref="ProcessExitReason.Shutdown"/>.
    /// </summary>
    public ProcessExitReason Reason { get; }

    /// <summary>Gets the simulated time of the request.</summary>
    public DateTimeOffset RequestedAt { get; }

    /// <summary>Gets the simulated time at which the OS forces the process if it is still running (<c>SIGKILL</c>).</summary>
    public DateTimeOffset Deadline { get; }

    /// <summary>Gets the grace period the OS gave, from <see cref="RequestedAt"/> to <see cref="Deadline"/>.</summary>
    public TimeSpan GracePeriod => Deadline - RequestedAt;

    /// <summary>
    /// Asks the OS to wait for <paramref name="work"/> (a recovery draft being written, the current
    /// step of a copy) before it applies its default action — closing the window, ending the
    /// service — like a suspension deferral. The OS never waits past <see cref="Deadline"/>: past it,
    /// the process is killed whatever its deferred work.
    /// </summary>
    /// <param name="work">Work that must finish before the process ends.</param>
    public void Defer(Task work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_deferred)
        {
            _deferred.Add(work);
        }
    }

    /// <summary>
    /// Completes when every deferred work has ended, successfully or not (the OS only waits; a
    /// failure is the app's to report).
    /// </summary>
    internal Task WhenDeferredWorkEndedAsync()
    {
        Task[] work;
        lock (_deferred)
        {
            work = [.. _deferred];
        }

        return work.Length == 0
            ? Task.CompletedTask
            : Task.WhenAll(work).ContinueWith(static _ => { }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private readonly List<Task> _deferred = [];
}
