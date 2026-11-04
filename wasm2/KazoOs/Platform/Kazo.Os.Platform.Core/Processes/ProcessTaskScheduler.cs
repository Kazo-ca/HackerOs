using Kazo.Os.AppSdk.Diagnostics;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>
/// The scheduler of one process (ADR 0069, section 3): every continuation the process schedules
/// runs through it, and once the process is killed it refuses them, so the process's leftover code
/// never runs again. It is the <c>SIGKILL</c> of a process that shares its thread with the OS: no
/// thread can be destroyed, but the process's next steps can be thrown away.
/// </summary>
/// <remarks>
/// <para>
/// It works in two layers, because <c>await</c> prefers the current
/// <see cref="System.Threading.SynchronizationContext"/> to <see cref="TaskScheduler.Current"/>:
/// <see cref="SynchronizationContext"/> is installed while the process's code runs, so its awaits post
/// here; tasks it starts with <c>Task.Factory.StartNew</c> or <c>ContinueWith</c> without a scheduler
/// find this one as <see cref="TaskScheduler.Current"/>. Both hand the work to the host's own context
/// (the Blazor renderer's, a test runner's) or, when there is none (Blazor WebAssembly), to the
/// thread pool: the process gains no thread, it only goes through a gate.
/// </para>
/// <para>
/// Each continuation it runs starts a new time slice for <see cref="KazoProcess.CheckpointAsync"/>:
/// it is the moment the process gets the thread back, a context switch.
/// </para>
/// <para>
/// Code that leaves the process on purpose (<c>ConfigureAwait(false)</c>, <c>Task.Run</c>) is not
/// held back here; it still reaches the OS only through gateways, which refuse a killed process.
/// Window apps keep running on the renderer's dispatcher: their windows are closed instead.
/// </para>
/// </remarks>
public sealed class ProcessTaskScheduler : TaskScheduler
{
    private readonly ProcessControlBlock _owner;
    private readonly ProcessSynchronizationContext _context;
    private SynchronizationContext? _host;
    private Task? _entryTask;

    internal ProcessTaskScheduler(ProcessControlBlock owner)
    {
        _owner = owner;
        _context = new ProcessSynchronizationContext(this);
    }

    /// <summary>Gets the synchronization context installed while the process's code runs.</summary>
    public SynchronizationContext SynchronizationContext => _context;

    /// <summary>Gets the PID of the owning process.</summary>
    internal long ProcessId => _owner.Pid.Value;

    /// <summary>
    /// Runs <paramref name="entryPoint"/> inline on the calling thread, as a task of this scheduler and
    /// under its synchronization context, then restores the caller's context.
    /// </summary>
    internal Task<T> RunInline<T>(Func<Task<T>> entryPoint)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        // A process started from another process's code (a pipeline in a script) dispatches to the
        // same host, not through its parent's gate: a child survives its parent's SIGKILL.
        _host ??= previous is ProcessSynchronizationContext parent ? parent.Scheduler._host : previous;

        Task<Task<T>> start = new(entryPoint);
        _entryTask = start;
        try
        {
            SynchronizationContext.SetSynchronizationContext(_context);
            KazoProcessSlice.Begin(ProcessId);
            start.RunSynchronously(this);
        }
        finally
        {
            _entryTask = null;
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        return start.Unwrap();
    }

    /// <summary>Posts one callback of the synchronization context as a task of this scheduler.</summary>
    internal void Post(SendOrPostCallback callback, object? state)
    {
        if (Drop())
        {
            return;
        }

        new Task(static posted => ((PostedCallback)posted!).Invoke(), new PostedCallback(callback, state)).Start(this);
    }

    /// <summary>Runs one callback inline under the synchronization context (<c>Send</c>).</summary>
    internal void Send(SendOrPostCallback callback, object? state)
    {
        _owner.ThrowIfKilled();
        RunUnderContext(static s => ((PostedCallback)s!).Invoke(), new PostedCallback(callback, state));
    }

    /// <inheritdoc />
    protected override void QueueTask(Task task)
    {
        if (Drop())
        {
            return;
        }

        if (_host is { } host)
        {
            host.Post(static s => ((ScheduledTask)s!).Run(), new ScheduledTask(this, task));
        }
        else
        {
            ThreadPool.UnsafeQueueUserWorkItem(static s => s.Run(), new ScheduledTask(this, task), preferLocal: false);
        }
    }

    /// <inheritdoc />
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
    {
        // The entry point always runs: RunSynchronously would otherwise block the thread waiting for it.
        if (ReferenceEquals(task, _entryTask))
        {
            return TryExecuteTask(task);
        }

        if (taskWasPreviouslyQueued || _owner.IsKilled)
        {
            return false;
        }

        bool executed = false;
        RunUnderContext(_ => executed = TryExecuteTask(task), null);
        return executed;
    }

    /// <inheritdoc />
    protected override IEnumerable<Task> GetScheduledTasks() => [];

    /// <summary>Drops the work when the process was killed, and counts it.</summary>
    private bool Drop()
    {
        if (!_owner.IsKilled)
        {
            return false;
        }

        _owner.CountDropped();
        return true;
    }

    /// <summary>Runs one task dequeued from the host, unless the process was killed in between.</summary>
    private void Execute(Task task)
    {
        if (Drop())
        {
            return;
        }

        RunUnderContext(_ => TryExecuteTask(task), null);
    }

    /// <summary>Runs code as the process: its synchronization context installed, a new time slice started.</summary>
    private void RunUnderContext(SendOrPostCallback callback, object? state)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(_context);
            KazoProcessSlice.Begin(ProcessId);
            callback(state);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed record PostedCallback(SendOrPostCallback Callback, object? State)
    {
        public void Invoke() => Callback(State);
    }

    private sealed class ScheduledTask(ProcessTaskScheduler scheduler, Task task)
    {
        public void Run() => scheduler.Execute(task);
    }

    /// <summary>The synchronization context of one process: <c>await</c> posts its continuations here.</summary>
    private sealed class ProcessSynchronizationContext(ProcessTaskScheduler scheduler) : SynchronizationContext
    {
        public ProcessTaskScheduler Scheduler { get; } = scheduler;

        public override void Post(SendOrPostCallback d, object? state) => Scheduler.Post(d, state);

        public override void Send(SendOrPostCallback d, object? state) => Scheduler.Send(d, state);

        public override SynchronizationContext CreateCopy() => this;
    }
}
