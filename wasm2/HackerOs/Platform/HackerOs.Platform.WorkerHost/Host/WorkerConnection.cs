using System.Diagnostics;
using System.Text.Json;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.WorkerHost.Protocol;
using HackerOs.Simulation.Abstractions.Workers;

namespace HackerOs.Platform.WorkerHost.Host;

/// <summary>
/// One Web Worker as the OS sees it (ADR 0070 and 0071): its role (a virtual core of the pool, an
/// overflow core, a dedicated worker or the always-ready one), its heartbeat, the processes it hosts,
/// and the measurements its heartbeat carries.
/// </summary>
internal sealed class WorkerConnection
{
    private readonly WorkerPool _pool;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, RemoteProcessSession> _sessions = [];
    private readonly Dictionary<long, ProcessSample> _samples = [];
    private readonly TaskCompletionSource _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IWebWorker? _worker;
    private long _heartbeatMemory;
    private long _browserMemory;
    private long _browserMemoryAt;
    private long _lastHeartbeat;
    private bool _yieldsProven;
    private long _emptySince;
    private bool _terminated;

    public WorkerConnection(WorkerPool pool, int id, WorkerRole role, int? core, DateTimeOffset startedAtUtc)
    {
        _pool = pool;
        Id = id;
        Role = role;
        Core = core;
        StartedAtUtc = startedAtUtc;
        _emptySince = Stopwatch.GetTimestamp();
    }

    /// <summary>Gets the worker number.</summary>
    public int Id { get; }

    /// <summary>Gets the runtime's id of the Web Worker behind this connection (<see cref="IWebWorker.Id"/>), once attached.</summary>
    public int? WorkerId => _worker?.Id;

    /// <summary>Gets or sets what the worker is for (changes when the ready worker is taken).</summary>
    public WorkerRole Role { get; set; }

    /// <summary>Gets or sets the virtual core a pool worker embodies.</summary>
    public int? Core { get; set; }

    /// <summary>Gets when the worker was created (simulated clock).</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>Gets the task that completes once the worker's runtime and assemblies are loaded.</summary>
    public Task Ready => _hello.Task;

    /// <summary>Gets whether the worker finished warming up.</summary>
    public bool IsReady => _hello.Task.IsCompletedSuccessfully && !IsTerminated;

    /// <summary>Gets whether the worker was terminated or died.</summary>
    public bool IsTerminated
    {
        get
        {
            lock (_gate)
            {
                return _terminated;
            }
        }
    }

    /// <summary>
    /// Gets the whole memory of the worker: the browser's own measurement when a recent one exists
    /// (ADR 0131, section 4; ADR 0126, section 6), else its runtime's at the last heartbeat (its
    /// WebAssembly memory, else its managed heap).
    /// </summary>
    public long RuntimeMemoryBytes => IsMemoryMeasuredByBrowser ? Interlocked.Read(ref _browserMemory) : _heartbeatMemory;

    /// <summary>Gets whether <see cref="RuntimeMemoryBytes"/> is the browser's measurement (fresh within three periods).</summary>
    public bool IsMemoryMeasuredByBrowser
    {
        get
        {
            long at = Interlocked.Read(ref _browserMemoryAt);
            return at != 0 && Stopwatch.GetElapsedTime(at) <= _pool.Options.MemoryMeasurePeriod * 3;
        }
    }

    /// <summary>Records the browser's measurement of this worker (<c>measureUserAgentSpecificMemory</c>).</summary>
    /// <param name="bytes">Bytes the browser attributes to the worker.</param>
    public void ApplyBrowserMemory(long bytes)
    {
        Interlocked.Exchange(ref _browserMemory, Math.Max(0, bytes));
        Interlocked.Exchange(ref _browserMemoryAt, Stopwatch.GetTimestamp());
    }

    /// <summary>Gets the managed heap of the worker's runtime at the last heartbeat (ADR 0126, section 5).</summary>
    public long RuntimeHeapBytes { get; private set; }

    /// <summary>
    /// Gets or sets whether the worker is replaced once it hosts no process (ADR 0142): it hosted an
    /// OOM victim, and its WebAssembly memory, grown by the victim, never shrinks.
    /// </summary>
    public bool RecycleWhenIdle { get; set; }

    /// <summary>Gets the CPU time the worker's thread spent running processes, from its heartbeats.</summary>
    public TimeSpan BusyTime { get; private set; }

    /// <summary>
    /// Gets the time since the last heartbeat that counts as busy: a worker hosting processes that
    /// misses its heartbeat is running one of them without yielding (its meter cannot report until it
    /// yields), so the kernel counts that time for them — as a looping process shows 100 % in
    /// <c>top</c>. Zero while the heartbeat is on time.
    /// </summary>
    public TimeSpan StalledTime =>
        ProcessCount > 0 && HeartbeatAge is { } age && age > _pool.Options.BusyThreshold
            ? age - _pool.Options.HeartbeatPeriod
            : TimeSpan.Zero;

    /// <summary>Gets the number of processes it hosts.</summary>
    public int ProcessCount
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>Gets the PIDs it hosts.</summary>
    public IReadOnlyList<long> ProcessIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions.Keys.Order()];
            }
        }
    }

    /// <summary>Gets the time since the last heartbeat, or <see langword="null"/> before the first.</summary>
    public TimeSpan? HeartbeatAge
    {
        get
        {
            long last = Interlocked.Read(ref _lastHeartbeat);
            return last == 0 ? null : Stopwatch.GetElapsedTime(last);
        }
    }

    /// <summary>
    /// Gets whether the worker's thread is stuck: it hosts a process and sent no heartbeat for longer
    /// than the unresponsive threshold (ADR 0070, section 5).
    /// </summary>
    public bool IsNotResponding =>
        !IsTerminated && ProcessCount > 0 && HeartbeatAge is { } age && age > _pool.Options.UnresponsiveThreshold;

    /// <summary>
    /// Gets whether the worker's thread can take a new process now (ADR 0071, section 3, ADR 0072): it
    /// is warmed up, answered its heartbeat recently — so it is not running a long slice — and its
    /// last heartbeat reports every process it hosts as running its own code for at least one period,
    /// proving each of them yields. Without the last rule a command that spins at once (<c>stress</c>)
    /// would look free until its worker's heartbeat went stale, and the next command would queue
    /// behind it.
    /// </summary>
    /// <remarks>
    /// The proof is what the worker reports, not when its heartbeat arrives: a heartbeat sent while the
    /// new process's assembly was still loading (that load yields) can arrive a period after the
    /// placement on a busy machine, though the process's code has not run yet.
    /// </remarks>
    public bool IsFree
    {
        get
        {
            if (!IsReady || ProcessCount == 0)
            {
                return IsReady;
            }

            long last = Interlocked.Read(ref _lastHeartbeat);
            bool yieldsProven;
            lock (_gate)
            {
                yieldsProven = _yieldsProven;
            }

            return last != 0
                && Stopwatch.GetElapsedTime(last) <= _pool.Options.BusyThreshold
                && yieldsProven;
        }
    }

    /// <summary>Gets the life-cycle state shown in <c>/proc/hackeros/workers</c>.</summary>
    public WorkerState State =>
        IsTerminated ? WorkerState.Terminated
        : !IsReady ? WorkerState.Starting
        : IsNotResponding ? WorkerState.NotResponding
        : ProcessCount > 0 ? WorkerState.Running
        : WorkerState.Idle;

    /// <summary>Gets how long the worker has hosted no process (for the overflow's return to size).</summary>
    public TimeSpan EmptyFor
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count > 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_emptySince);
            }
        }
    }

    /// <summary>Binds the started worker and sends it <c>init</c>.</summary>
    public void Bind(IWebWorker worker)
    {
        _worker = worker;
        worker.MessageReceived += OnMessage;
        worker.Faulted += reason => _pool.OnWorkerDied(this, reason);
        Post(WorkerMessage.Create(WorkerMessageType.Init, writer =>
        {
            writer.WriteNumber("w", Id);
            writer.WriteNumber("hb", (int)_pool.Options.HeartbeatPeriod.TotalMilliseconds);
            writer.WriteBoolean("cpu", _pool.Options.MeterCpu);
            WireCodec.WriteStrings(writer, "pre", _pool.Options.PreloadAssemblies);
            WireCodec.WriteStrings(writer, "known", _pool.Options.KnownAssemblies);
        }));
    }

    /// <summary>Fails the warm-up (the worker could not start).</summary>
    public void FailStart(Exception exception) => _hello.TrySetException(exception);

    /// <summary>Posts one message to the worker.</summary>
    public void Post(string message)
    {
        if (!IsTerminated)
        {
            _worker?.Post(message);
        }
    }

    /// <summary>
    /// Answers a synchronous call (ADR 0131, section 4) in the worker's shared slot, where its thread
    /// waits in <c>Atomics.wait</c>. An answer too large for the slot is replaced by an I/O error; a
    /// late one (the worker gave up) is dropped, as a late <c>result</c> of an abandoned call is.
    /// </summary>
    /// <param name="callId">The call's number.</param>
    /// <param name="reply">Its <c>result</c> message.</param>
    public void AnswerSynchronously(int callId, string reply)
    {
        if (IsTerminated || _worker is not ISynchronousWebWorker synchronous)
        {
            return;
        }

        if (synchronous.AnswerSynchronousCall(callId, reply) == SynchronousAnswerOutcome.TooLarge)
        {
            synchronous.AnswerSynchronousCall(callId, WorkerMessage.Create(WorkerMessageType.Result, writer =>
            {
                writer.WriteNumber("i", callId);
                RemoteErrors.Write(writer, new IOException("The answer is too large for a synchronous call; use the asynchronous member."));
            }));
        }
    }

    /// <summary>Gets how many processes this worker has hosted since it started.</summary>
    public int HostedTotal { get; private set; }

    /// <summary>Registers a process placed on this worker.</summary>
    public void Add(RemoteProcessSession session)
    {
        lock (_gate)
        {
            _sessions[session.Pid] = session;
            HostedTotal++;
            // Until a heartbeat shows its code running and the thread coming back (ADR 0072, section 6).
            _yieldsProven = false;
        }
    }

    /// <summary>Gets the measured times of a hosted process from the last heartbeat.</summary>
    public ProcessSample? GetSample(long pid)
    {
        lock (_gate)
        {
            return _samples.TryGetValue(pid, out ProcessSample sample) ? sample : null;
        }
    }

    /// <summary>Gets whether this worker hosts <paramref name="pid"/>.</summary>
    public bool Hosts(long pid)
    {
        lock (_gate)
        {
            return _sessions.ContainsKey(pid);
        }
    }

    /// <summary>
    /// Escalation after a <c>SIGKILL</c> (ADR 0069, section 3): the worker has until the kill timeout
    /// to confirm it dropped the process (step 3). A worker that stays mute has its thread stuck in
    /// the process's code: it is terminated (step 4).
    /// </summary>
    public async Task EnsureKilledAsync(RemoteProcessSession session)
    {
        await Task.Delay(_pool.Options.KillAcknowledgeTimeout).ConfigureAwait(false);
        if (session.HasEnded || session.KillAcknowledged || IsTerminated)
        {
            return;
        }

        _pool.TerminateWorker(this, session.Pid);
    }

    /// <summary>
    /// Terminates the worker (<c>worker.terminate()</c>) and ends every process it hosted as killed.
    /// </summary>
    /// <returns>The processes that were still running on it.</returns>
    /// <param name="beforeAbandon">
    /// Called with the hosted processes once the worker is terminated, before they end: the pool marks
    /// them killed in the process table first, so whoever waits for them sees why they ended.
    /// </param>
    public IReadOnlyList<RemoteProcessSession> Terminate(Action<IReadOnlyList<RemoteProcessSession>>? beforeAbandon = null)
    {
        List<RemoteProcessSession> sessions;
        lock (_gate)
        {
            if (_terminated)
            {
                return [];
            }

            _terminated = true;
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        _worker?.Terminate();
        _hello.TrySetException(new WebWorkerStartException($"Worker {Id} was terminated."));
        beforeAbandon?.Invoke(sessions);
        foreach (RemoteProcessSession session in sessions)
        {
            session.Abandon();
        }

        return sessions;
    }

    /// <summary>Writes a worker diagnostic to <c>/var/log/syslog</c>.</summary>
    public void Log(string message) => _pool.Log($"worker {Id}: {message}");

    private void OnMessage(string message)
    {
        try
        {
            using JsonDocument document = WorkerMessage.Parse(message, out string type);
            JsonElement root = document.RootElement;
            switch (type)
            {
                case WorkerMessageType.Hello:
                    Interlocked.Exchange(ref _lastHeartbeat, Stopwatch.GetTimestamp());
                    _hello.TrySetResult();
                    _pool.OnWorkerReady(this);
                    break;
                case WorkerMessageType.Heartbeat:
                    OnHeartbeat(root);
                    break;
                case WorkerMessageType.Call:
                {
                    bool synchronous = root.GetFlag("s");
                    if (Find(root) is { } caller)
                    {
                        _ = caller.OnCallAsync(root.GetInt32("i"), root.GetString("o"), root.GetProperty("a").Clone(), synchronous);
                    }
                    else
                    {
                        string killed = WorkerMessage.Create(WorkerMessageType.Result, writer =>
                        {
                            writer.WriteNumber("i", root.GetInt32("i"));
                            RemoteErrors.Write(writer, new HackerOs.AppSdk.Diagnostics.HackProcessKilledException(root.GetInt64("p")));
                        });
                        if (synchronous)
                        {
                            AnswerSynchronously(root.GetInt32("i"), killed);
                        }
                        else
                        {
                            Post(killed);
                        }
                    }

                    break;
                }
                case WorkerMessageType.Notify:
                    if (Find(root) is { } notifier)
                    {
                        _ = notifier.OnNotifyAsync(root.GetString("o"), root.GetProperty("a").Clone());
                    }

                    break;
                case WorkerMessageType.Cancel:
                    foreach (RemoteProcessSession session in Snapshot())
                    {
                        session.OnCancel(root.GetInt32("i"));
                    }

                    break;
                case WorkerMessageType.Output:
                    Find(root)?.OnOutput(root.GetInt32("f"), root.GetString("d"));
                    break;
                case WorkerMessageType.Exit:
                    OnExit(root);
                    break;
                case WorkerMessageType.KillAck:
                    Find(root)?.OnKillAcknowledged();
                    break;
                case WorkerMessageType.StopDone:
                    Find(root)?.OnStopDone();
                    break;
                case WorkerMessageType.ServiceStopped:
                    Find(root)?.OnServiceStopped();
                    break;
                case WorkerMessageType.Log:
                    _pool.Log(root.GetString("m"));
                    break;
                case WorkerMessageType.UiOutput:
                    Find(root)?.UiReceiver?.Invoke(root.GetProperty("d"));
                    break;
                case WorkerMessageType.HttpResponse:
                    Find(root)?.OnHttpResponse(root.GetInt32("sk"), root.GetInt64("x"), WireCodec.ReadListenerReply(root.GetProperty("r")));
                    break;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log($"bad message: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private void OnHeartbeat(JsonElement root)
    {
        Interlocked.Exchange(ref _lastHeartbeat, Stopwatch.GetTimestamp());
        // The runtime's whole memory when the platform reports it, else its managed heap.
        long heap = root.GetInt64("heap");
        RuntimeHeapBytes = heap;
        _heartbeatMemory = root.GetOptional("rss") is { } rss && rss.GetInt64() > heap ? rss.GetInt64() : heap;
        if (root.GetOptional("busy") is { } busy)
        {
            BusyTime = TimeSpan.FromTicks(busy.GetInt64());
        }

        lock (_gate)
        {
            HashSet<long> yielding = [];
            foreach (JsonElement process in root.GetProperty("ps").EnumerateArray())
            {
                if (process.GetOptional("rn") is { } running && TimeSpan.FromTicks(running.GetInt64()) >= _pool.Options.HeartbeatPeriod)
                {
                    yielding.Add(process.GetInt64("p"));
                }

                if (process.TryGetProperty("ut", out _))
                {
                    _samples[process.GetInt64("p")] = new ProcessSample(
                        TimeSpan.FromTicks(process.GetInt64("ut")),
                        TimeSpan.FromTicks(process.GetInt64("st")),
                        process.GetInt64("lt"),
                        TimeSpan.FromTicks(process.GetInt64("ls")),
                        process.GetInt64("cs"),
                        process.GetInt64("al"),
                        Stopwatch.GetTimestamp());
                }
            }

            _yieldsProven = _sessions.Keys.All(yielding.Contains);
        }

        // The answer keeps the worker's simulated clock in step (ADR 0012).
        _pool.SendClock(this);
    }

    private void OnExit(JsonElement root)
    {
        long pid = root.GetInt64("p");
        RemoteProcessSession? session = Find(root);
        if (session is null)
        {
            return;
        }

        RemoteProcessExit exit = new(
            (RemoteProcessExitStatus)root.GetInt32("s"), root.GetInt32("c"), root.GetOptionalString("m"))
        {
            // ADR 0128: the result, fault or refusal of a background task.
            JobResult = root.GetOptionalString("jr"),
            JobFault = root.GetOptional("jf") is { } fault
                ? new HackerOs.AppSdk.Threading.HackBackgroundJobException(
                    fault.GetOptionalString("ty") ?? "System.Exception", fault.GetOptionalString("m") ?? string.Empty, fault.GetOptionalString("st"))
                : null,
            JobRejected = root.GetFlag("jx")
        };
        _ = CompleteAsync();

        async Task CompleteAsync()
        {
            await session.OnExitAsync(exit).ConfigureAwait(false);
            lock (_gate)
            {
                _sessions.Remove(pid);
                _samples.Remove(pid);
                if (_sessions.Count == 0)
                {
                    _emptySince = Stopwatch.GetTimestamp();
                }
            }

            _pool.OnProcessEnded(this);
        }
    }

    private RemoteProcessSession? Find(JsonElement root)
    {
        long pid = root.GetInt64("p");
        lock (_gate)
        {
            return _sessions.GetValueOrDefault(pid);
        }
    }

    private List<RemoteProcessSession> Snapshot()
    {
        lock (_gate)
        {
            return [.. _sessions.Values];
        }
    }
}

/// <summary>The measurements of one process from its worker's last heartbeat.</summary>
internal readonly record struct ProcessSample(
    TimeSpan UserTime,
    TimeSpan SystemTime,
    long LongTasks,
    TimeSpan LongestSlice,
    long Slices,
    long AllocatedBytes,
    long ReceivedAt);
