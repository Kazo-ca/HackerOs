using HackerOs.Platform.Core.Lifecycle;
using HackerOs.Platform.WorkerHost.Protocol;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Time;
using HackerOs.Simulation.Abstractions.Workers;

namespace HackerOs.Platform.WorkerHost.Host;

/// <summary>
/// The Web Workers of the machine (ADR 0070, sections 2, 3 and 5; ADR 0071): one worker per virtual
/// core for commands and pipelines, a worker of its own for each dedicated process, and — once the
/// system is started — always at least one ready worker, replaced as soon as it is taken.
/// </summary>
/// <remarks>
/// <para><b>Placement.</b> A command goes to the free pool worker hosting the fewest processes; every
/// stage of one pipeline goes to the same worker. When no pool worker is free (all are running a
/// slice or are not responding), the ready worker joins the pool as an <c>overflow</c> core, like a
/// machine adding hardware threads under load; an overflow worker empty for 30 s is removed.</para>
/// <para><b>Ceiling.</b> At most <c>navigator.hardwareConcurrency</c> workers (at least 2). At the
/// ceiling the invariant is suspended: a request waits for a worker to free up, and
/// <c>/var/log/syslog</c> notes it once ("worker pool saturated").</para>
/// <para><b>Escalation step 4.</b> A worker that does not confirm a kill is terminated: its other
/// processes end as killed, each with a syslog line, and the ready worker takes its core.</para>
/// </remarks>
public sealed class WorkerPool : IWorkerPoolStatus, HackerOs.Platform.Core.Processes.IWorkerMemoryReclaimer, IAsyncDisposable
{
    private readonly IWebWorkerRuntime _runtime;
    private readonly ISimulationClock _clock;
    private readonly Func<IProcessManager?> _processes;
    private readonly Action<string, long?> _log;
    private readonly SystemStartupSignal? _startup;
    private readonly Lock _gate = new();
    private readonly List<WorkerConnection> _workers = [];
    private readonly Dictionary<Guid, PlacementGroup> _groups = [];
    private readonly WorkerRunQueue _runQueue = new();
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextId;
    private bool _started;
    private bool _saturationLogged;
    private Task? _watchdog;
    private Task? _memoryMeter;

    /// <summary>Initializes the pool; nothing starts before <see cref="StartAsync"/> or the first request.</summary>
    /// <param name="runtime">Starts the Web Workers.</param>
    /// <param name="options">Timings and sizes.</param>
    /// <param name="clock">The simulated clock, sent to the workers with each heartbeat answer.</param>
    /// <param name="processes">The process table, to end the neighbors of a terminated worker as killed.</param>
    /// <param name="log">Writes one line to <c>/var/log/syslog</c> (message, optional PID).</param>
    /// <param name="startup">The "system started" signal (ADR 0071, section 2); the pool warms up after it.</param>
    public WorkerPool(
        IWebWorkerRuntime runtime,
        WorkerPoolOptions options,
        ISimulationClock clock,
        Func<IProcessManager?>? processes = null,
        Action<string, long?>? log = null,
        SystemStartupSignal? startup = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _processes = processes ?? (static () => null);
        _log = log ?? (static (_, _) => { });
        _startup = startup;
        Capacity = Math.Max(2, runtime.HardwareConcurrency);
        PoolSize = Math.Clamp(options.VirtualCores, 1, Capacity - 1);
        if (startup is not null)
        {
            _ = startup.Started.ContinueWith(
                static (_, state) => ((WorkerPool)state!).StartAsync(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>Gets the pool's settings.</summary>
    public WorkerPoolOptions Options { get; }

    /// <inheritdoc />
    public int Capacity { get; }

    /// <inheritdoc />
    public int PoolSize { get; }

    /// <summary>Gets whether the system-started warm-up has begun.</summary>
    public bool IsStarted
    {
        get
        {
            lock (_gate)
            {
                return _started;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<WorkerSnapshot> GetWorkers()
    {
        lock (_gate)
        {
            return [.. _workers.OrderBy(static worker => worker.Id).Select(static worker => new WorkerSnapshot(
                worker.Id, worker.Role, worker.State, worker.Core, worker.ProcessIds,
                worker.RuntimeMemoryBytes, worker.StartedAtUtc, worker.HeartbeatAge, worker.BusyTime + worker.StalledTime)
            {
                RuntimeHeapBytes = worker.RuntimeHeapBytes
            })];
        }
    }

    /// <summary>
    /// The system is started (ADR 0071, section 2): once the browser is idle (at most 2 s), start the
    /// ready worker, then the pool, worker by worker, behind it. Idempotent.
    /// </summary>
    public async Task StartAsync()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        try
        {
            await _runtime.WhenIdleAsync(Options.IdleWaitTimeout, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _watchdog ??= WatchAsync(_lifetime.Token);
        if (_runtime is IWebWorkerMemoryMeter meter)
        {
            _memoryMeter ??= MeasureMemoryAsync(meter, _lifetime.Token);
        }

        EnsureReadyWorker();
        while (!_lifetime.IsCancellationRequested)
        {
            WorkerConnection? next;
            lock (_gate)
            {
                int pool = _workers.Count(static worker => worker.Role == WorkerRole.Pool && !worker.IsTerminated);
                next = pool < PoolSize && LiveCount() < Capacity ? Create(WorkerRole.Pool, NextFreeCore()) : null;
            }

            if (next is null)
            {
                return;
            }

            try
            {
                await LaunchAsync(next).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log($"worker {next.Id}: could not start: {exception.Message}");
                return;
            }
        }
    }

    /// <summary>
    /// Finds the worker a command (or a pipeline stage) runs on (ADR 0070, section 2; ADR 0071,
    /// section 3), in the order of the run queue (ADR 0127, section 1).
    /// </summary>
    /// <remarks>
    /// <para><b>Run queue.</b> Every request waits in one queue ordered by niceness, then by
    /// arrival: when a core frees up, the lowest niceness takes it, like the kernel picking the
    /// next task of its run queue. A request never jumps ahead of an earlier one of the same
    /// niceness, so the order is fair.</para>
    /// <para><b>The ready worker.</b> A process with a positive niceness does not take the ready
    /// worker while a pool worker is still warming up for it: the ready worker stays for the
    /// processes that cannot wait (the next dedicated process, a niceness of zero or less).</para>
    /// </remarks>
    /// <param name="group">The pipeline it belongs to, if any.</param>
    /// <param name="nice">The niceness of the process to place.</param>
    /// <param name="cancellationToken">Abandons the wait (Ctrl+C).</param>
    /// <returns>A warmed-up worker; the caller then places the process on it.</returns>
    internal async Task<WorkerConnection> AcquireForCommandAsync(Guid? group, int nice, CancellationToken cancellationToken)
    {
        _watchdog ??= WatchAsync(_lifetime.Token);
        WorkerRunQueue.Entry entry;
        lock (_gate)
        {
            entry = _runQueue.Enqueue(nice);
        }

        try
        {
            while (true)
            {
                Task changed;
                WorkerConnection? starting = null;
                lock (_gate)
                {
                    if (group is { } id && _groups.TryGetValue(id, out PlacementGroup? placement) && !placement.Worker.IsTerminated)
                    {
                        // A later stage of a pipeline joins its first stage: it does not queue for a core.
                        placement.Count++;
                        return placement.Worker;
                    }

                    bool headOfQueue = _runQueue.IsHead(entry);
                    WorkerConnection? chosen = headOfQueue
                        ? _workers
                            .Where(static worker => worker.Role is WorkerRole.Pool or WorkerRole.Overflow && worker.IsFree)
                            .OrderBy(static worker => worker.ProcessCount)
                            .ThenBy(static worker => worker.BusyTime)
                            .ThenBy(static worker => worker.Id)
                            .FirstOrDefault()
                        : null;
                    WorkerConnection? warming = _workers.FirstOrDefault(static worker =>
                        worker.Role is WorkerRole.Pool or WorkerRole.Overflow && !worker.IsTerminated && !worker.IsReady);
                    bool mayTakeReady = WorkerRunQueue.MayTakeReadyWorker(entry.Nice, warming is not null);
                    if (headOfQueue && chosen is null && mayTakeReady && TakeReadyWorker() is { } ready)
                    {
                        // ADR 0071, section 3: no core is free, the ready worker joins the pool for this process.
                        int pool = _workers.Count(static worker => worker.Role == WorkerRole.Pool && !worker.IsTerminated);
                        if (pool < PoolSize)
                        {
                            ready.Role = WorkerRole.Pool;
                            ready.Core = NextFreeCore();
                        }
                        else
                        {
                            ready.Role = WorkerRole.Overflow;
                            ready.Core = null;
                        }

                        chosen = ready;
                    }

                    if (chosen is not null)
                    {
                        Track(group, chosen);
                        _ = Task.Run(EnsureReadyWorker, CancellationToken.None);
                        return chosen;
                    }

                    // A pool worker or the ready worker still warming up will be free soonest.
                    starting = warming ?? _workers.FirstOrDefault(static worker =>
                        worker.Role == WorkerRole.Ready && !worker.IsTerminated && !worker.IsReady);
                    if (headOfQueue && starting is null && LiveCount() < Capacity)
                    {
                        // Before the system is started (or while the pool fills up), a request starts its own worker.
                        int pool = _workers.Count(static worker => worker.Role == WorkerRole.Pool && !worker.IsTerminated);
                        starting = pool < PoolSize ? Create(WorkerRole.Pool, NextFreeCore()) : Create(WorkerRole.Overflow, null);
                        _ = LaunchQuietlyAsync(starting);
                    }
                    else if (headOfQueue && starting is null)
                    {
                        NoteSaturated();
                    }

                    changed = _changed.Task;
                }

                Task wait = starting is null ? changed : Task.WhenAny(starting.Ready, changed);
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate)
            {
                _runQueue.Remove(entry);
            }

            // The next request in the queue may now take a core.
            Signal();
        }
    }

    /// <summary>Gets the niceness of a process about to be placed (ADR 0127); <c>0</c> without a process table.</summary>
    internal int NiceOf(ProcessId pid)
    {
        try
        {
            return _processes()?.GetNice(pid) ?? ProcessNice.Default;
        }
        catch (KeyNotFoundException)
        {
            return ProcessNice.Default;
        }
    }

    /// <summary>Gets the number of commands waiting for a core (diagnostics and tests).</summary>
    internal int RunQueueLength
    {
        get
        {
            lock (_gate)
            {
                return _runQueue.Count;
            }
        }
    }

    /// <summary>
    /// Finds a worker of its own for a <c>Dedicated</c> process (ADR 0070, section 3): the ready
    /// worker, replaced at once (ADR 0071, section 3).
    /// </summary>
    internal async Task<WorkerConnection> AcquireDedicatedAsync(CancellationToken cancellationToken)
    {
        _watchdog ??= WatchAsync(_lifetime.Token);
        while (true)
        {
            Task changed;
            WorkerConnection? starting;
            lock (_gate)
            {
                if (TakeReadyWorker() is { } ready)
                {
                    ready.Role = WorkerRole.Dedicated;
                    ready.Core = null;
                    _ = Task.Run(EnsureReadyWorker, CancellationToken.None);
                    return ready;
                }

                starting = _workers.FirstOrDefault(static worker => worker.Role == WorkerRole.Ready && !worker.IsTerminated);
                if (starting is null && LiveCount() < Capacity)
                {
                    starting = Create(WorkerRole.Ready, null);
                    _ = LaunchQuietlyAsync(starting);
                }
                else if (starting is null)
                {
                    NoteSaturated();
                }

                changed = _changed.Task;
            }

            Task wait = starting is null ? changed : Task.WhenAny(starting.Ready, changed);
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Finds the web worker (ADR 0074): the one worker every web app runs in, like an IIS
    /// application pool. The first site takes the ready worker (replaced at once); the next ones join it.
    /// </summary>
    internal async Task<WorkerConnection> AcquireWebAsync(CancellationToken cancellationToken)
    {
        _watchdog ??= WatchAsync(_lifetime.Token);
        while (true)
        {
            Task changed;
            WorkerConnection? starting;
            lock (_gate)
            {
                starting = _workers.FirstOrDefault(static worker => worker.Role == WorkerRole.Web && !worker.IsTerminated);
                if (starting is { IsReady: true })
                {
                    return starting;
                }

                if (starting is null && TakeReadyWorker() is { } ready)
                {
                    ready.Role = WorkerRole.Web;
                    ready.Core = null;
                    _ = Task.Run(EnsureReadyWorker, CancellationToken.None);
                    return ready;
                }

                if (starting is null && LiveCount() < Capacity)
                {
                    starting = Create(WorkerRole.Web, null);
                    _ = LaunchQuietlyAsync(starting);
                }
                else if (starting is null)
                {
                    NoteSaturated();
                }

                changed = _changed.Task;
            }

            Task wait = starting is null ? changed : Task.WhenAny(starting.Ready, changed);
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Releases a pipeline placement taken by <see cref="AcquireForCommandAsync"/>.</summary>
    internal void Release(Guid? group)
    {
        if (group is not { } id)
        {
            return;
        }

        lock (_gate)
        {
            if (_groups.TryGetValue(id, out PlacementGroup? placement) && --placement.Count <= 0)
            {
                _groups.Remove(id);
            }
        }
    }

    /// <summary>Gets the live workers (diagnostics and tests).</summary>
    internal IReadOnlyList<WorkerConnection> Connections
    {
        get
        {
            lock (_gate)
            {
                return [.. _workers];
            }
        }
    }

    /// <summary>Gets the worker hosting <paramref name="pid"/>, if any.</summary>
    internal WorkerConnection? FindHost(long pid)
    {
        lock (_gate)
        {
            return _workers.FirstOrDefault(worker => worker.Hosts(pid));
        }
    }

    /// <summary>
    /// Escalation step 4 (ADR 0069, section 3; ADR 0070, section 5): terminates a worker that did not
    /// answer, ends its other processes as killed with a syslog line each, and replaces it.
    /// </summary>
    /// <param name="worker">The mute worker.</param>
    /// <param name="triggerPid">The process whose kill it did not confirm.</param>
    internal void TerminateWorker(WorkerConnection worker, long triggerPid) =>
        Retire(worker, $"killed: worker {worker.Id} terminated to stop pid {triggerPid}", triggerPid);

    /// <summary>The worker died on its own (script error, runtime abort): same as a termination.</summary>
    internal void OnWorkerDied(WorkerConnection worker, string reason)
    {
        Log($"worker {worker.Id}: died: {reason}");
        Retire(worker, $"killed: worker {worker.Id} died", triggerPid: null);
    }

    internal void OnWorkerReady(WorkerConnection worker) => Signal();

    /// <inheritdoc />
    public bool RecycleAfterOomKill(long pid)
    {
        if (FindHost(pid) is not { } worker)
        {
            return false;
        }

        worker.RecycleWhenIdle = true;
        Log($"worker {worker.Id}: hosts OOM victim pid {pid}, replaced once idle to give its memory back", pid);
        return true;
    }

    /// <summary>A process placed on <paramref name="worker"/> ended.</summary>
    internal void OnProcessEnded(WorkerConnection worker)
    {
        if (worker.RecycleWhenIdle && worker.Role is WorkerRole.Pool or WorkerRole.Overflow && worker.ProcessCount == 0 && !worker.IsTerminated)
        {
            // ADR 0142: the OOM victim is gone; a new worker gives its memory back to the browser.
            Retire(worker, $"killed: worker {worker.Id} recycled after an OOM kill", triggerPid: null);
            return;
        }

        if (worker.Role is WorkerRole.Dedicated or WorkerRole.Web && worker.ProcessCount == 0)
        {
            // ADR 0070, section 3: a dedicated process that ends frees its worker; the web worker
            // ends with its last site (ADR 0074).
            lock (_gate)
            {
                _workers.Remove(worker);
            }

            worker.Terminate();
        }

        Signal();
        EnsureReadyWorker();
    }

    /// <summary>Answers a heartbeat with the simulated clock.</summary>
    internal void SendClock(WorkerConnection worker) =>
        worker.Post(WorkerMessage.Create(WorkerMessageType.Clock, writer =>
        {
            WireCodec.WriteTime(writer, "u", _clock.UtcNow);
            writer.WriteNumber("k", _clock.CurrentTick);
        }));

    internal void Log(string message, long? pid = null) => _log(message, pid);

    /// <summary>Gets the simulated clock (start messages carry it).</summary>
    internal ISimulationClock Clock => _clock;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        List<WorkerConnection> workers;
        lock (_gate)
        {
            workers = [.. _workers];
            _workers.Clear();
        }

        foreach (WorkerConnection worker in workers)
        {
            worker.Terminate();
        }

        if (_watchdog is { } watchdog)
        {
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        _lifetime.Dispose();
    }

    /// <summary>
    /// Keeps the invariant of ADR 0071: once the system is started, at least one ready worker exists
    /// (starting or warmed up), unless the ceiling is reached.
    /// </summary>
    internal void EnsureReadyWorker()
    {
        WorkerConnection? created = null;
        lock (_gate)
        {
            if (!_started || _lifetime.IsCancellationRequested)
            {
                return;
            }

            if (_workers.Any(static worker => worker.Role == WorkerRole.Ready && !worker.IsTerminated))
            {
                return;
            }

            if (LiveCount() >= Capacity)
            {
                NoteSaturated();
                return;
            }

            _saturationLogged = false;
            created = Create(WorkerRole.Ready, null);
        }

        _ = LaunchQuietlyAsync(created);
    }

    private void Retire(WorkerConnection worker, string neighborMessage, long? triggerPid)
    {
        WorkerRole role = worker.Role;
        int? core = worker.Core;
        lock (_gate)
        {
            _workers.Remove(worker);
            foreach (Guid group in _groups.Where(entry => ReferenceEquals(entry.Value.Worker, worker)).Select(static entry => entry.Key).ToList())
            {
                _groups.Remove(group);
            }
        }

        // The neighbors are killed in the process table before their sessions end: otherwise their
        // command could complete with 137 first, and the shell would not see the reason (no "Killed").
        worker.Terminate(sessions => KillNeighbors(worker, sessions, neighborMessage, triggerPid));

        if (role == WorkerRole.Pool)
        {
            // The ready worker takes the lost core at once (ADR 0071, section 3).
            WorkerConnection? replacement;
            lock (_gate)
            {
                replacement = TakeReadyWorker();
                if (replacement is not null)
                {
                    replacement.Role = WorkerRole.Pool;
                    replacement.Core = core;
                }
                else if (LiveCount() < Capacity && _started)
                {
                    replacement = Create(WorkerRole.Pool, core);
                    _ = LaunchQuietlyAsync(replacement);
                }
            }
        }

        Signal();
        EnsureReadyWorker();
    }

    /// <summary>ADR 0070, section 5: the neighbors end as killed, with the reason in syslog.</summary>
    private void KillNeighbors(WorkerConnection worker, IReadOnlyList<RemoteProcessSession> sessions, string neighborMessage, long? triggerPid)
    {
        Log(triggerPid is { } pid ? $"worker {worker.Id} terminated: pid {pid} did not stop" : $"worker {worker.Id} terminated", triggerPid);
        IProcessManager? processes = _processes();
        foreach (RemoteProcessSession session in sessions.Where(session => session.Pid != triggerPid))
        {
            Log(neighborMessage, session.Pid);
            if (processes is not null && processes.TryGetActive(ProcessId.FromInt64(session.Pid), out _))
            {
                try
                {
                    processes.Kill(ProcessId.FromInt64(session.Pid), ProcessExitReason.Killed);
                }
                catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
                {
                    // Ended meanwhile.
                }
            }
        }
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        HashSet<int> notResponding = [];
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(Options.HeartbeatPeriod, cancellationToken).ConfigureAwait(false);
            List<WorkerConnection> workers;
            lock (_gate)
            {
                workers = [.. _workers];
            }

            foreach (WorkerConnection worker in workers)
            {
                if (worker.IsNotResponding)
                {
                    if (notResponding.Add(worker.Id))
                    {
                        Log($"worker {worker.Id}: not responding (pids {string.Join(' ', worker.ProcessIds)})");
                    }
                }
                else if (notResponding.Remove(worker.Id))
                {
                    Signal();
                }

                if (worker.Role == WorkerRole.Overflow && worker.ProcessCount == 0 && worker.EmptyFor >= Options.OverflowIdleTimeout)
                {
                    // ADR 0071, section 3: the pool returns to its size.
                    lock (_gate)
                    {
                        _workers.Remove(worker);
                    }

                    worker.Terminate();
                    Signal();
                    EnsureReadyWorker();
                }
            }

            if (workers.Any(static worker => worker.IsFree))
            {
                Signal();
            }
        }
    }

    /// <summary>
    /// ADR 0131, section 4: on a cross-origin isolated page the browser measures each worker
    /// (<c>performance.measureUserAgentSpecificMemory()</c>); its figure replaces the heartbeat's in
    /// <c>/proc</c> and for the OOM killer (ADR 0126, section 6). Stops for good when the browser
    /// does not offer the meter.
    /// </summary>
    private async Task MeasureMemoryAsync(IWebWorkerMemoryMeter meter, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IReadOnlyDictionary<int, long>? measured = await meter.MeasureWorkersAsync(cancellationToken).ConfigureAwait(false);
                if (measured is null)
                {
                    return;
                }

                List<WorkerConnection> workers;
                lock (_gate)
                {
                    workers = [.. _workers];
                }

                foreach (WorkerConnection worker in workers)
                {
                    if (worker.WorkerId is { } id && measured.TryGetValue(id, out long bytes))
                    {
                        worker.ApplyBrowserMemory(bytes);
                    }
                }

                await Task.Delay(Options.MemoryMeasurePeriod, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The pool is disposed.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log($"memory measurement stopped: {exception.Message}");
        }
    }

    private WorkerConnection? TakeReadyWorker() =>
        _workers.FirstOrDefault(static worker => worker.Role == WorkerRole.Ready && worker.IsReady && worker.ProcessCount == 0);

    private void Track(Guid? group, WorkerConnection worker)
    {
        if (group is { } id)
        {
            _groups[id] = new PlacementGroup(worker) { Count = 1 };
        }
    }

    private WorkerConnection Create(WorkerRole role, int? core)
    {
        WorkerConnection worker = new(this, ++_nextId, role, core, _clock.UtcNow);
        _workers.Add(worker);
        return worker;
    }

    private int LiveCount() => _workers.Count(static worker => !worker.IsTerminated);

    private int NextFreeCore()
    {
        HashSet<int> used = [.. _workers.Where(static worker => worker.Role == WorkerRole.Pool && !worker.IsTerminated && worker.Core is not null).Select(static worker => worker.Core!.Value)];
        int core = 0;
        while (used.Contains(core))
        {
            core++;
        }

        return core;
    }

    private void NoteSaturated()
    {
        if (!_saturationLogged)
        {
            _saturationLogged = true;
            Log($"worker pool saturated: {Capacity} workers (navigator.hardwareConcurrency), requests wait for a free worker");
        }
    }

    private async Task LaunchAsync(WorkerConnection worker)
    {
        IWebWorker started;
        try
        {
            started = await _runtime.StartAsync(
                new WebWorkerStartOptions($"hackeros-worker-{worker.Id}", Options.EntryAssembly, Options.PreloadAssemblies),
                _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_gate)
            {
                _workers.Remove(worker);
            }

            worker.FailStart(exception);
            Signal();
            throw;
        }

        if (worker.IsTerminated)
        {
            started.Terminate();
            return;
        }

        worker.Bind(started);
        await worker.Ready.ConfigureAwait(false);
    }

    private async Task LaunchQuietlyAsync(WorkerConnection worker)
    {
        try
        {
            await LaunchAsync(worker).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!_lifetime.IsCancellationRequested)
            {
                Log($"worker {worker.Id}: could not start: {exception.Message}");
            }
        }
    }

    private void Signal()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            previous = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        previous.TrySetResult();
    }

    /// <summary>One command waiting for a core (ADR 0127): its niceness and its place in the arrival order.</summary>

    private sealed class PlacementGroup(WorkerConnection worker)
    {
        public WorkerConnection Worker { get; } = worker;

        public int Count { get; set; }
    }
}
