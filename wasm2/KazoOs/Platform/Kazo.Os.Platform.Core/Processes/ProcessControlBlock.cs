using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>
/// The kernel-side state of one process that outlives its <see cref="ProcessRecord"/> snapshots
/// (ADR 0069), like a Linux <c>task_struct</c>: the pending stop request (<c>SIGTERM</c>), the
/// killed flag (<c>SIGKILL</c>) and the process's own scheduler.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InMemoryProcessManager"/> creates one per process and drives it: <see cref="TryRequestStop"/>
/// and <see cref="RaiseStopRequested"/> during <c>StopAsync</c>, <see cref="MarkKilled"/> when the
/// process is forced (<see cref="ProcessExitReason.Killed"/>, or <see cref="ProcessExitReason.Timeout"/>
/// at the end of a grace period). The app execution context reads it to raise
/// <c>StopRequested</c> and to refuse every gateway call of a killed process.
/// </para>
/// <para>
/// Once killed, the block never comes back: the PID is never reused during a boot (ADR 0012), and
/// leftover code of the process keeps hitting <see cref="HackProcessKilledException"/>.
/// </para>
/// </remarks>
public sealed class ProcessControlBlock
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _killed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ProcessStopRequest? _stopRequest;
    private bool _stopRaised;
    private long _droppedContinuations;

    /// <summary>Initializes the control block of one process.</summary>
    /// <param name="pid">The process's PID.</param>
    public ProcessControlBlock(ProcessId pid)
    {
        Pid = pid;
        Scheduler = new ProcessTaskScheduler(this);
    }

    /// <summary>Raised once, synchronously, when the process is first asked to stop (see <see cref="RaiseStopRequested"/>).</summary>
    public event Action<ProcessStopRequest>? StopRequested;

    /// <summary>Gets the PID.</summary>
    public ProcessId Pid { get; }

    /// <summary>Gets the process's own scheduler: its continuations run through it, and it drops them once the process is killed.</summary>
    public ProcessTaskScheduler Scheduler { get; }

    /// <summary>Gets the first stop request the process received, or <see langword="null"/>.</summary>
    public ProcessStopRequest? StopRequest
    {
        get
        {
            lock (_gate)
            {
                return _stopRequest;
            }
        }
    }

    /// <summary>Gets whether the OS killed the process (<c>SIGKILL</c>, or a grace period that ran out).</summary>
    public bool IsKilled => _killed.Task.IsCompleted;

    /// <summary>Gets a task that completes when the process is killed.</summary>
    public Task Killed => _killed.Task;

    /// <summary>Gets how many continuations of the process the scheduler dropped after it was killed.</summary>
    public long DroppedContinuations => Interlocked.Read(ref _droppedContinuations);

    /// <summary>
    /// Records the first stop request. A later request (two <c>kill</c> in a row) keeps the first
    /// reason and deadline, like a pending signal that is not queued twice.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns><see langword="true"/> when this was the first request.</returns>
    public bool TryRequestStop(ProcessStopRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_stopRequest is not null || IsKilled)
            {
                return false;
            }

            _stopRequest = request;
            return true;
        }
    }

    /// <summary>
    /// Delivers the recorded stop request to <see cref="StopRequested"/>, once. The manager calls it
    /// outside its own lock, after the process moved to <c>Stopping</c> and before it cancels the
    /// process's token (ADR 0069, section 1). A handler that throws does not stop the others.
    /// </summary>
    public void RaiseStopRequested()
    {
        ProcessStopRequest? request;
        lock (_gate)
        {
            if (_stopRaised || _stopRequest is null)
            {
                return;
            }

            _stopRaised = true;
            request = _stopRequest;
        }

        if (StopRequested is not { } handlers)
        {
            return;
        }

        foreach (Action<ProcessStopRequest> handler in handlers.GetInvocationList().Cast<Action<ProcessStopRequest>>())
        {
            try
            {
                handler(request);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A faulty handler must not keep the OS from stopping the process.
            }
        }
    }

    /// <summary>
    /// Marks the process killed (ADR 0069, section 3, step 3): from now on its scheduler drops every
    /// new continuation, its gateways throw <see cref="HackProcessKilledException"/>, and
    /// <see cref="RunAsync{T}"/> stops waiting for it.
    /// </summary>
    public void MarkKilled() => _killed.TrySetResult();

    /// <summary>Throws <see cref="HackProcessKilledException"/> when the process was killed.</summary>
    public void ThrowIfKilled()
    {
        if (IsKilled)
        {
            throw new HackProcessKilledException(Pid.Value);
        }
    }

    /// <summary>
    /// Runs the entry point of a headless process (command, service) on the process's own scheduler
    /// (ADR 0069, section 3): its code starts inline on the caller's thread, and every continuation it
    /// schedules without <c>ConfigureAwait(false)</c> goes through <see cref="Scheduler"/>.
    /// </summary>
    /// <typeparam name="T">Result of the entry point.</typeparam>
    /// <param name="entryPoint">Starts the process's work.</param>
    /// <returns>
    /// The entry point's result; faults with <see cref="HackProcessKilledException"/> as soon as the
    /// process is killed, without waiting for its code, whose continuations are dropped.
    /// </returns>
    public Task<T> RunAsync<T>(Func<Task<T>> entryPoint)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
        ThrowIfKilled();

        Task<T> work = Scheduler.RunInline(entryPoint);
        return work.IsCompleted ? work : WaitUnlessKilledAsync(work);
    }

    /// <summary>Counts one continuation the scheduler dropped.</summary>
    internal void CountDropped() => Interlocked.Increment(ref _droppedContinuations);

    private async Task<T> WaitUnlessKilledAsync<T>(Task<T> work)
    {
        Task first = await Task.WhenAny(work, _killed.Task).ConfigureAwait(false);
        if (first != work)
        {
            // The work's continuations are dropped: it will never complete. Observe a late fault
            // anyway so it never surfaces as an unobserved task exception.
            _ = work.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new HackProcessKilledException(Pid.Value);
        }

        return await work.ConfigureAwait(false);
    }
}
