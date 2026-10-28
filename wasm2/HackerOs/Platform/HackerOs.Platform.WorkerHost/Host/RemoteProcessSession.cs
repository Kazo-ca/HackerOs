using System.Text.Json;
using System.Threading.Channels;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Diagnostics;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.Processes;
using HackerOs.Platform.Core.Shell;
using HackerOs.Platform.WorkerHost.Protocol;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Processes;

namespace HackerOs.Platform.WorkerHost.Host;

/// <summary>
/// The OS side of one process running in a worker (ADR 0070, section 4): it serves the process's
/// remote calls through its trusted <see cref="IAppExecutionContext"/>, writes its output to the
/// shell's streams, feeds its standard input, and carries its stop request, interruption and kill to
/// the worker (ADR 0069).
/// </summary>
/// <remarks>
/// Every call is served inside the process's ambient frame, in the kernel sub-frame: the time the
/// OS spends serving it is the process's <c>stime</c>, like a system call (ADR 0067, section 2).
/// </remarks>
internal sealed class RemoteProcessSession
{
    /// <summary>A regular file up to this size goes to the worker in the first answer of a read.</summary>
    public const long InlineReadLimit = 16L * 1024 * 1024;

    private const int ChunkSize = 64 * 1024;

    private readonly WorkerConnection _worker;
    private readonly IAppExecutionContext _context;
    private readonly ProcessControlBlock? _control;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly Channel<(int Stream, string Text)> _outputQueue = Channel.CreateUnbounded<(int, string)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _outputPump;
    private readonly TaskCompletionSource<RemoteProcessExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopWorkDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _serviceStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private readonly Dictionary<int, CancellationTokenSource> _calls = [];
    private readonly Dictionary<int, FileSystemContentReadHandle> _reads = [];
    private readonly Dictionary<int, HackerOs.AppSdk.Net.IHackHttpListenerSocket> _listeners = [];
    private readonly Dictionary<int, IDisposable> _descriptors = [];
    private readonly CancellationTokenSource _listening = new();
    private readonly RemoteEventRouter _events;
    private int _nextReadHandle;
    private int _nextListener;
    private int _nextDescriptor;
    private bool _listenersClosed;
    private bool _stdoutClosed;
    private bool _stderrClosed;
    private bool _killAcknowledged;

    public RemoteProcessSession(
        WorkerConnection worker,
        RemoteProcessLaunch launch,
        TextReader input,
        TextWriter output,
        TextWriter error,
        bool inputAttached)
        : this(worker, launch.Context, launch.Control, input, output, error, inputAttached)
    {
        Launch = launch;
    }

    /// <summary>A background task's process (ADR 0128): no entry point, no streams of its own.</summary>
    public RemoteProcessSession(WorkerConnection worker, RemoteBackgroundJobLaunch job)
        : this(worker, job.Context, job.Control, TextReader.Null, TextWriter.Null, TextWriter.Null, inputAttached: false)
    {
        JobProgress = job.Progress;
    }

    private RemoteProcessSession(
        WorkerConnection worker,
        IAppExecutionContext context,
        ProcessControlBlock? control,
        TextReader input,
        TextWriter output,
        TextWriter error,
        bool inputAttached)
    {
        _worker = worker;
        _context = context;
        _control = control;
        _input = input;
        _output = output;
        _error = error;
        InputAttached = inputAttached;
        Pid = context.ProcessId.Value;
        _events = new RemoteEventRouter(Pid, worker.Post, worker.Log);
        _outputPump = PumpOutputAsync();
    }

    /// <summary>Gets the process.</summary>
    public long Pid { get; }

    /// <summary>Gets what the OS handed to the worker; <see langword="null"/> for a background task.</summary>
    public RemoteProcessLaunch? Launch { get; }

    /// <summary>Receives the reports of a background task (ADR 0128), or <see langword="null"/>.</summary>
    public Action<HackerOs.AppSdk.Threading.HackBackgroundProgress>? JobProgress { get; }

    /// <summary>
    /// Receives the <c>ui</c> outputs of a window rendered in the worker (ADR 0129 trial), on the
    /// page's thread; the element is valid during the call only. <see langword="null"/> otherwise.
    /// </summary>
    public Action<JsonElement>? UiReceiver { get; set; }

    /// <summary>Gets whether the process reads a real standard input (a pipe or the terminal).</summary>
    public bool InputAttached { get; }

    /// <summary>Gets the task that ends when the process has ended and all its output is written.</summary>
    public Task<RemoteProcessExit> Exit => _exit.Task;

    /// <summary>Gets whether the process has ended (or its worker was terminated).</summary>
    public bool HasEnded => _exit.Task.IsCompleted;

    /// <summary>Gets whether the worker confirmed it dropped the process after a kill.</summary>
    public bool KillAcknowledged
    {
        get
        {
            lock (_gate)
            {
                return _killAcknowledged;
            }
        }
    }

    /// <summary>Gets the process's open event subscriptions and watches (ADR 0124).</summary>
    public int EventSubscriptionCount => _events.Count;

    /// <summary>Gets the task the worker completes when <c>OnStoppingAsync</c> of a hosted service returned.</summary>
    public Task ServiceStopped => _serviceStopped.Task;

    /// <summary>Hooks the process's stop request, permission errors and kill to the worker.</summary>
    public void Attach()
    {
        _context.StopRequested += OnStopRequested;
        _context.IntentReceived += OnIntentReceived;
        _context.PermissionErrors.PermissionDenied += OnPermissionDenied;
        if (_context.StopRequest is { } already)
        {
            OnStopRequested(_context, already);
        }

        _control?.Killed.ContinueWith(
            static (_, state) => ((RemoteProcessSession)state!).OnKilled(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Forwards an intent the OS handed to this running process to its worker (ADR 0096, section 5).</summary>
    private void OnIntentReceived(object? sender, AppIntentDelivery delivery)
    {
        if (!HasEnded)
        {
            _worker.Post(WorkerMessage.Create(WorkerMessageType.Intent, writer =>
            {
                writer.WriteNumber("p", Pid);
                WireCodec.WriteIntentDelivery(writer, "it", delivery);
            }));
        }
    }

    /// <summary>Ctrl+C: cancels the process token in the worker.</summary>
    public void Interrupt()
    {
        if (!HasEnded)
        {
            _worker.Post(WorkerMessage.Create(WorkerMessageType.Interrupt, writer => writer.WriteNumber("p", Pid)));
        }
    }

    /// <summary>Asks a hosted service to run its <c>OnStoppingAsync</c>.</summary>
    public async Task StopServiceAsync(ServiceStopReason reason, CancellationToken cancellationToken)
    {
        if (HasEnded)
        {
            return;
        }

        _worker.Post(WorkerMessage.Create(WorkerMessageType.ServiceStop, writer =>
        {
            writer.WriteNumber("p", Pid);
            writer.WriteNumber("r", (int)reason);
        }));
        await Task.WhenAny(_serviceStopped.Task, _exit.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Handles the <c>exit</c> message: waits for the output already received, then ends.</summary>
    public async Task OnExitAsync(RemoteProcessExit exit)
    {
        _outputQueue.Writer.TryComplete();
        await _outputPump.ConfigureAwait(false);
        End(exit);
    }

    /// <summary>
    /// Ends the process because its worker is gone (terminated, or crashed): <c>SIGKILL</c> semantics,
    /// whatever it was doing (ADR 0070, section 5).
    /// </summary>
    /// <remarks>
    /// The output the worker sent before it was terminated is still written first, as for an
    /// <c>exit</c>, within <see cref="AbandonedOutputTimeout"/>: a reader that no longer reads does
    /// not keep the process alive.
    /// </remarks>
    public void Abandon()
    {
        _outputQueue.Writer.TryComplete();
        _ = EndAfterOutputAsync(new RemoteProcessExit(RemoteProcessExitStatus.Killed));
    }

    /// <summary>How long an abandoned process waits for the output already received to be written.</summary>
    internal static readonly TimeSpan AbandonedOutputTimeout = TimeSpan.FromSeconds(1);

    private async Task EndAfterOutputAsync(RemoteProcessExit exit)
    {
        try
        {
            await _outputPump.WaitAsync(AbandonedOutputTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The reader stopped reading: the process ends without the rest of its output.
        }

        End(exit);
    }

    /// <summary>Queues output text in arrival order.</summary>
    public void OnOutput(int stream, string text) => _outputQueue.Writer.TryWrite((stream, text));

    /// <summary>The work deferred on the stop request has ended in the worker.</summary>
    public void OnStopDone() => _stopWorkDone.TrySetResult();

    /// <summary>The service's <c>OnStoppingAsync</c> returned in the worker.</summary>
    public void OnServiceStopped() => _serviceStopped.TrySetResult();

    /// <summary>The worker dropped the process after a kill: it still answers.</summary>
    public void OnKillAcknowledged()
    {
        lock (_gate)
        {
            _killAcknowledged = true;
        }
    }

    /// <summary>The process answered a request of one of its listening sockets (<c>hres</c>).</summary>
    public void OnHttpResponse(int socketId, long exchangeId, HackerOs.AppSdk.Net.HackHttpListenerReply reply)
    {
        HackerOs.AppSdk.Net.IHackHttpListenerSocket? socket;
        lock (_gate)
        {
            _listeners.TryGetValue(socketId, out socket);
        }

        socket?.Respond(exchangeId, reply);
    }

    /// <summary>Abandons a call whose token the worker cancelled.</summary>
    public void OnCancel(int id)
    {
        CancellationTokenSource? call;
        lock (_gate)
        {
            _calls.Remove(id, out call);
        }

        call?.Cancel();
    }

    /// <summary>
    /// Serves one call and posts its answer; a <paramref name="synchronous"/> call (ADR 0131,
    /// section 4) is answered in the worker's shared slot instead, where its thread waits.
    /// </summary>
    public async Task OnCallAsync(int id, string operation, JsonElement arguments, bool synchronous = false)
    {
        using CancellationTokenSource cancellation = new();
        lock (_gate)
        {
            _calls[id] = cancellation;
        }

        string reply;
        try
        {
            Action<Utf8JsonWriter> value = await ServeAsync(operation, arguments, cancellation.Token).ConfigureAwait(false);
            reply = WorkerMessage.Create(WorkerMessageType.Result, writer =>
            {
                writer.WriteNumber("i", id);
                writer.WriteStartObject("v");
                value(writer);
                writer.WriteEndObject();
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reply = WorkerMessage.Create(WorkerMessageType.Result, writer =>
            {
                writer.WriteNumber("i", id);
                RemoteErrors.Write(writer, exception);
            });
        }
        finally
        {
            lock (_gate)
            {
                _calls.Remove(id);
            }
        }

        if (synchronous)
        {
            // ADR 0131, section 4: the worker waits in Atomics.wait; the answer goes to its shared slot.
            _worker.AnswerSynchronously(id, reply);
        }
        else
        {
            _worker.Post(reply);
        }
    }

    /// <summary>Serves one call that has no answer; a failure is logged, as nobody waits for it.</summary>
    public async Task OnNotifyAsync(string operation, JsonElement arguments)
    {
        try
        {
            await ServeAsync(operation, arguments, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _worker.Log($"pid {Pid}: {operation} failed: {exception.Message}");
        }
    }

    private void End(RemoteProcessExit exit)
    {
        _context.StopRequested -= OnStopRequested;
        _context.IntentReceived -= OnIntentReceived;
        _context.PermissionErrors.PermissionDenied -= OnPermissionDenied;
        List<FileSystemContentReadHandle> reads;
        List<CancellationTokenSource> calls;
        List<HackerOs.AppSdk.Net.IHackHttpListenerSocket> listeners;
        List<IDisposable> descriptors;
        lock (_gate)
        {
            descriptors = [.. _descriptors.Values];
            _descriptors.Clear();
            reads = [.. _reads.Values];
            _reads.Clear();
            calls = [.. _calls.Values];
            _calls.Clear();
            listeners = [.. _listeners.Values];
            _listeners.Clear();
            _listenersClosed = true;
        }

        // The kernel closes the sockets of a process that ends: the port is free again. Its
        // subscriptions leave the bus too, without waiting for the worker (ADR 0124, section 1).
        _listening.Cancel();
        _events.CloseAll();
        foreach (HackerOs.AppSdk.Net.IHackHttpListenerSocket listener in listeners)
        {
            _ = listener.DisposeAsync();
        }

        foreach (FileSystemContentReadHandle read in reads)
        {
            _ = read.DisposeAsync();
        }

        // exit(2) closes every descriptor the process still holds (ADR 0125).
        foreach (IDisposable descriptor in descriptors)
        {
            descriptor.Dispose();
        }

        foreach (CancellationTokenSource call in calls)
        {
            call.Cancel();
        }

        _stopWorkDone.TrySetResult();
        _serviceStopped.TrySetResult();
        _exit.TrySetResult(exit);
    }

    private void OnStopRequested(object? sender, HackStopRequestedEventArgs e)
    {
        // The OS waits for the work the app defers in the worker, within the grace period.
        e.Defer(_stopWorkDone.Task);
        _worker.Post(WorkerMessage.Create(WorkerMessageType.Stop, writer =>
        {
            writer.WriteNumber("p", Pid);
            writer.WriteNumber("r", (int)e.Reason);
            WireCodec.WriteTime(writer, "ra", e.RequestedAt);
            WireCodec.WriteTime(writer, "dl", e.Deadline);
        }));
    }

    private void OnPermissionDenied(object? sender, AppPermissionErrorEventArgs e) =>
        _worker.Post(WorkerMessage.Create(WorkerMessageType.PermissionDenied, writer =>
        {
            writer.WriteNumber("p", Pid);
            WireCodec.WriteError(writer, "err", e.Error);
        }));

    /// <summary>
    /// <c>SIGKILL</c> (ADR 0069, section 3): step 3 asks the worker to drop the process; if the
    /// worker does not answer, step 4 terminates it (<see cref="WorkerConnection.EnsureKilledAsync"/>).
    /// </summary>
    private void OnKilled()
    {
        if (HasEnded)
        {
            return;
        }

        // A killed process receives nothing more, even if its worker is slow to drop it.
        _events.CloseAll();
        _worker.Post(WorkerMessage.Create(WorkerMessageType.Kill, writer => writer.WriteNumber("p", Pid)));
        _ = _worker.EnsureKilledAsync(this);
    }

    private async Task PumpOutputAsync()
    {
        await foreach ((int stream, string text) in _outputQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            bool closed;
            lock (_gate)
            {
                closed = stream == 1 ? _stdoutClosed : _stderrClosed;
            }

            if (!closed)
            {
                try
                {
                    await (stream == 1 ? _output : _error).WriteAsync(text).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ShellBrokenPipeException or ObjectDisposedException)
                {
                    // ADR 0050, section 4: the reader went away; the next write in the worker fails.
                    lock (_gate)
                    {
                        if (stream == 1)
                        {
                            _stdoutClosed = true;
                        }
                        else
                        {
                            _stderrClosed = true;
                        }
                    }

                    _worker.Post(WorkerMessage.Create(WorkerMessageType.PipeClosed, writer =>
                    {
                        writer.WriteNumber("p", Pid);
                        writer.WriteNumber("f", stream);
                    }));
                }
            }

            _worker.Post(WorkerMessage.Create(WorkerMessageType.OutputAck, writer =>
            {
                writer.WriteNumber("p", Pid);
                writer.WriteNumber("n", text.Length);
            }));
        }
    }

    /// <summary>
    /// ADR 0128 section 6: a process in a worker starts a background task. The OS places it like any
    /// other (the pool, or the main thread) through this process's own gateway, and forwards its
    /// reports to the worker as <c>bgp</c> messages keyed by the caller's job number <c>j</c>.
    /// </summary>
    private async Task<Action<Utf8JsonWriter>> RunBackgroundJobAsync(JsonElement a, CancellationToken cancellationToken)
    {
        JsonElement target = a.GetProperty("jt");
        HackerOs.AppSdk.Threading.HackBackgroundJobRequest request = new(
            new HackerOs.AppSdk.Threading.HackBackgroundJobTarget(
                target.GetString("asm"), target.GetString("ty"), target.GetOptionalString("me"), target.GetString("it")),
            a.GetString("ji"),
            a.GetOptionalString("n"),
            a.GetOptional("ni")?.GetInt32() ?? 0);
        int job = a.GetInt32("j");
        Action<HackerOs.AppSdk.Threading.HackBackgroundProgress>? progress = a.GetFlag("pg")
            ? report => _worker.Post(WorkerMessage.Create(WorkerMessageType.BackgroundJobProgress, writer =>
            {
                writer.WriteNumber("p", Pid);
                writer.WriteNumber("j", job);
                writer.WriteNumber("f", report.Fraction);
                if (report.Message is { } message)
                {
                    writer.WriteString("m", message);
                }
            }))
            : null;
        string? result = await _context.Background.RunAsync(request, progress, cancellationToken).ConfigureAwait(false);
        return writer =>
        {
            if (result is not null)
            {
                writer.WriteString("r", result);
            }
        };
    }

    /// <summary>
    /// Opens a listening socket for the process (ADR 0074, section 2) and starts carrying its
    /// requests to the worker as <c>hreq</c> messages; the worker buffers a request that arrives
    /// before the answer naming the socket.
    /// </summary>
    private async Task<Action<Utf8JsonWriter>> ListenAsync(IReadOnlyList<string> prefixes, CancellationToken cancellationToken)
    {
        var result = await _context.Network.ListenHttpAsync(prefixes, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return writer => WireCodec.WriteNetwork(writer, result, static (_, _) => { });
        }

        HackerOs.AppSdk.Net.IHackHttpListenerSocket socket = result.Value!;
        int id;
        lock (_gate)
        {
            if (_listenersClosed)
            {
                _ = socket.DisposeAsync();
                throw new HackProcessKilledException(Pid);
            }

            id = ++_nextListener;
            _listeners[id] = socket;
        }

        _ = PumpListenerAsync(id, socket);
        return writer =>
        {
            writer.WriteNumber("ne", (int)HackerOs.AppSdk.Net.HackNetworkError.None);
            writer.WriteNumber("sk", id);
            WireCodec.WriteStrings(writer, "px", socket.Prefixes);
        };
    }

    /// <summary>Hands each request of one socket to the worker until the socket closes.</summary>
    private async Task PumpListenerAsync(int id, HackerOs.AppSdk.Net.IHackHttpListenerSocket socket)
    {
        try
        {
            while (await socket.AcceptAsync(_listening.Token).ConfigureAwait(false) is { } exchange)
            {
                _worker.Post(WorkerMessage.Create(WorkerMessageType.HttpRequest, writer =>
                {
                    writer.WriteNumber("p", Pid);
                    writer.WriteNumber("sk", id);
                    WireCodec.WriteListenerExchange(writer, "ex", exchange);
                }));
            }
        }
        catch (OperationCanceledException)
        {
            // The process ended.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _worker.Log($"pid {Pid}: listening socket {id} failed: {exception.Message}");
        }
    }

    /// <summary>Runs one operation through the process's own gateways and returns the writer of its value.</summary>
    private async Task<Action<Utf8JsonWriter>> ServeAsync(string operation, JsonElement a, CancellationToken cancellationToken)
    {
        using IDisposable frame = HackExecutionContext.Enter(_context);
        using HackExecutionContext.KernelScope kernel = HackExecutionContext.EnterKernel();
        IAppExecutionContext c = _context;
        switch (operation)
        {
            case RemoteOps.FsRead:
                return await ReadAsync(WireCodec.ReadRead(a), cancellationToken).ConfigureAwait(false);
            case RemoteOps.FsReadChunk:
                return await ReadChunkAsync(a.GetInt32("h"), a.GetInt32("n"), cancellationToken).ConfigureAwait(false);
            case RemoteOps.FsReadClose:
                CloseRead(a.GetInt32("h"));
                return static _ => { };
            case RemoteOps.FsEnumerate:
            {
                FileSystemResult<FileSystemDirectorySnapshot> result = await c.FileSystem.EnumerateAsync(WireCodec.ReadEnumerate(a), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteResult(writer, result, WireCodec.WriteDirectory);
            }
            case RemoteOps.FsStat:
            {
                FileSystemResult<FileSystemEntrySnapshot> result = await c.FileSystem.StatAsync(WireCodec.ReadStat(a), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteResult(writer, result, static (w, snapshot) => WireCodec.WriteSnapshot(w, "en", snapshot));
            }
            case RemoteOps.FsCreate:
                return Mutation(await c.FileSystem.CreateAsync(WireCodec.ReadCreate(a), cancellationToken).ConfigureAwait(false));
            case RemoteOps.FsWrite:
            {
                (FileSystemWriteRequest request, FileSystemContentDescriptor descriptor, byte[] content) = WireCodec.ReadWrite(a);
                return Mutation(await c.FileSystem.WriteAsync(request, new BytesContentSource(descriptor, content), cancellationToken).ConfigureAwait(false));
            }
            case RemoteOps.FsMove:
                return Mutation(await c.FileSystem.MoveAsync(WireCodec.ReadMove(a), cancellationToken).ConfigureAwait(false));
            case RemoteOps.FsCopy:
                return Mutation(await c.FileSystem.CopyAsync(WireCodec.ReadCopy(a), cancellationToken).ConfigureAwait(false));
            case RemoteOps.FsDelete:
                return Mutation(await c.FileSystem.DeleteAsync(WireCodec.ReadDelete(a), cancellationToken).ConfigureAwait(false));
            case RemoteOps.FsSetPermissions:
                return Mutation(await c.FileSystem.SetPermissionsAsync(WireCodec.ReadSetPermissions(a), cancellationToken).ConfigureAwait(false));

            case RemoteOps.StdinRead:
            {
                char[] buffer = new char[Math.Clamp(a.GetInt32("n"), 1, ChunkSize)];
                int read = await _input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                string? text = read == 0 ? null : new string(buffer, 0, read);
                return writer =>
                {
                    if (text is null)
                    {
                        writer.WriteBoolean("eof", true);
                    }
                    else
                    {
                        writer.WriteString("d", text);
                    }
                };
            }

            case RemoteOps.BackgroundProgress:
                // ADR 0128 section 4: the task's own process reports; already throttled in the worker.
                JobProgress?.Invoke(new HackerOs.AppSdk.Threading.HackBackgroundProgress(
                    Math.Clamp(a.GetDouble("f"), 0d, 1d), a.GetOptionalString("m")));
                return static _ => { };
            case RemoteOps.BackgroundRun:
                return await RunBackgroundJobAsync(a, cancellationToken).ConfigureAwait(false);

            case RemoteOps.EnvSetDirectory:
                c.Environment.SetCurrentDirectory(WireCodec.ReadPath(a, "p"));
                return static _ => { };
            case RemoteOps.EnvSetVariable:
                c.Environment.SetVariable(a.GetString("n"), a.GetOptionalString("v"));
                return static _ => { };
            case RemoteOps.ServiceNotify:
                // The trusted context of this process only accepts it if it is a service (ADR 0097).
                c.ServiceNotify.Notify(WireCodec.ReadServiceNotification(a));
                return static _ => { };
            case RemoteOps.Log:
                c.Logging.Log(
                    (HackerOs.Simulation.Abstractions.Diagnostics.DiagnosticSeverity)a.GetInt32("s"),
                    a.GetString("m"),
                    a.TryGetProperty("pr", out _) ? WireCodec.ReadDictionary(a, "pr") : null);
                return static _ => { };
            case RemoteOps.LogFile:
                try
                {
                    c.Logging.AppendToLogFile(a.GetString("p"), a.GetString("m"));
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException)
                {
                    // A fire-and-forget line the OS refuses is dropped, like a datagram to a closed syslog socket.
                }

                return static _ => { };
            case RemoteOps.LogSite:
                try
                {
                    c.Logging.AppendToSiteDebugLog(a.GetString("m"));
                }
                catch (UnauthorizedAccessException)
                {
                    // Same as LogFile: a line the OS refuses is dropped.
                }

                return static _ => { };
            case RemoteOps.NotificationPost:
                c.Notifications.Post(
                    (HackerOs.Simulation.Abstractions.Notifications.NotificationSeverity)a.GetInt32("s"),
                    a.GetString("ti"),
                    a.GetString("m"),
                    a.GetOptional("ac") is { } actions
                        ? [.. WireCodec.ReadPairs(actions).Select(static pair => new HackerOs.Simulation.Abstractions.Notifications.NotificationAction(pair.Key, pair.Value))]
                        : null,
                    a.GetOptional("ex") is { } expiry ? TimeSpan.FromTicks(expiry.GetInt64()) : null);
                return static _ => { };
            case RemoteOps.ClockDelay:
                await c.Clock.DelayAsync(TimeSpan.FromTicks(a.GetInt64("d")), cancellationToken).ConfigureAwait(false);
                return static _ => { };

            case RemoteOps.ProcessStop:
            {
                ProcessRecord stopped = await c.Processes.StopAsync(
                    ProcessId.FromInt64(a.GetInt64("pid")), TimeSpan.FromTicks(a.GetInt64("to")),
                    (ProcessExitReason)a.GetInt32("r"), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteProcess(writer, "pr", stopped);
            }
            case RemoteOps.ProcessList:
            {
                IReadOnlyList<ProcessRecord> processes = c.Processes.ListProcesses();
                return writer => WireCodec.WriteProcesses(writer, "ps", processes);
            }
            case RemoteOps.ProcessKill:
            {
                ProcessRecord killed = await c.Processes.KillAsync(
                    ProcessId.FromInt64(a.GetInt64("pid")), (ProcessExitReason)a.GetInt32("r"), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteProcess(writer, "pr", killed);
            }

            case RemoteOps.NetResolve:
            {
                var result = await c.Network.ResolveAsync(a.GetString("h"), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteNetwork(writer, result, WireCodec.WriteHostEntry);
            }
            case RemoteOps.NetReverse:
            {
                var result = await c.Network.ReverseLookupAsync(System.Net.IPAddress.Parse(a.GetString("ad")), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteNetwork(writer, result, WireCodec.WriteHostEntry);
            }
            case RemoteOps.NetPing:
            {
                var result = await c.Network.PingAsync(a.GetString("h"), TimeSpan.FromTicks(a.GetInt64("to")), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteNetwork(writer, result, WireCodec.WritePingReply);
            }
            case RemoteOps.NetHttp:
            {
                var result = await c.Network.SendAsync(WireCodec.ReadHttpRequest(a), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteNetwork(writer, result, WireCodec.WriteHttpResponse);
            }
            case RemoteOps.NetTcp:
            {
                var result = await c.Network.ProbeTcpAsync(a.GetString("h"), a.GetInt32("f"), a.GetInt32("l"), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteNetwork(writer, result, WireCodec.WritePortProbes);
            }
            case RemoteOps.NetOs:
            {
                var result = await c.Network.DetectOperatingSystemAsync(a.GetString("h"), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteNetwork(writer, result, WireCodec.WriteFingerprint);
            }
            case RemoteOps.NetListen:
                return await ListenAsync(WireCodec.ReadStrings(a, "px"), cancellationToken).ConfigureAwait(false);
            case RemoteOps.NetUnlisten:
            {
                HackerOs.AppSdk.Net.IHackHttpListenerSocket? socket;
                lock (_gate)
                {
                    _listeners.Remove(a.GetInt32("sk"), out socket);
                }

                if (socket is not null)
                {
                    await socket.DisposeAsync().ConfigureAwait(false);
                }

                return static _ => { };
            }
            case RemoteOps.NetListeners:
            {
                var listeners = await c.Network.GetTcpListenersAsync(cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteListeners(writer, listeners);
            }

            case RemoteOps.CatalogInstalled:
            {
                var apps = await c.Catalog.GetInstalledAsync(cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteApps(writer, apps);
            }
            case RemoteOps.CatalogHandlers:
            {
                var apps = await c.Catalog.GetHandlersAsync(WireCodec.ReadPath(a, "p"), a.GetOptionalString("mt"), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteApps(writer, apps);
            }
            case RemoteOps.CatalogDefaults:
            {
                var associations = await c.Catalog.GetDefaultHandlersAsync(cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteAssociations(writer, associations);
            }
            case RemoteOps.CatalogSetDefault:
            {
                HackerOs.AppSdk.Apps.HackDefaultHandlerStatus status = await c.Catalog.SetUserDefaultHandlerAsync(
                    a.GetString("t"), a.GetString("a"), a.GetOptionalString("id"), cancellationToken).ConfigureAwait(false);
                return writer => writer.WriteNumber("s", (int)status);
            }
            case RemoteOps.ServiceStart:
                return ServiceResult(await c.Services.StartAsync(a.GetString("id"), cancellationToken).ConfigureAwait(false));
            case RemoteOps.ServiceStop:
                return ServiceResult(await c.Services.StopAsync(a.GetString("id"), cancellationToken).ConfigureAwait(false));
            case RemoteOps.ServiceGetMode:
            {
                ServiceStartMode mode = await c.Services.GetStartModeAsync(a.GetString("id"), cancellationToken).ConfigureAwait(false);
                return writer => writer.WriteNumber("m", (int)mode);
            }
            case RemoteOps.ServiceSetMode:
                return ServiceResult(await c.Services.SetStartModeAsync(a.GetString("id"), (ServiceStartMode)a.GetInt32("m"), cancellationToken).ConfigureAwait(false));
            case RemoteOps.ServiceStatus:
            {
                ServiceStatusSnapshot status = await c.Services.GetStatusAsync(a.GetString("id"), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteServiceStatus(writer, status);
            }
            case RemoteOps.ServiceResetFailed:
                return ServiceResult(await c.Services.ResetFailedAsync(a.GetString("id"), cancellationToken).ConfigureAwait(false));
            case RemoteOps.Drives:
            {
                var drives = await c.Drives.GetDrivesAsync(cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteDrives(writer, drives);
            }
            case RemoteOps.DriveHostSupported:
            {
                bool supported = await c.Drives.IsHostFolderSupportedAsync(cancellationToken).ConfigureAwait(false);
                return writer => writer.WriteBoolean("s", supported);
            }
            case RemoteOps.DriveMount:
            {
                HackerOs.AppSdk.IO.HackDriveSnapshot? drive = await c.Drives.MountHostFolderAsync(a.GetOptionalString("p"), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteDrive(writer, drive);
            }
            case RemoteOps.DriveReconnect:
            {
                bool ready = await c.Drives.ReconnectAsync(a.GetString("p"), cancellationToken).ConfigureAwait(false);
                return writer => writer.WriteBoolean("s", ready);
            }
            case RemoteOps.DriveUnmount:
                await c.Drives.UnmountAsync(a.GetString("p"), cancellationToken).ConfigureAwait(false);
                return static _ => { };
            case RemoteOps.CrossOriginIsolation:
            {
                HackCrossOriginIsolationStatus isolation = await c.CrossOriginIsolation.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteCrossOriginIsolation(writer, isolation);
            }
            case RemoteOps.IntentLaunch:
            {
                AppIntentLaunchResult result = await c.Intents.LaunchAsync(a.GetString("id"), WireCodec.ReadStrings(a, "ar"), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteLaunchResult(writer, result);
            }
            case RemoteOps.IntentOpenFile:
            {
                AppIntentOpenFileResult result = await c.Intents.OpenFileAsync(WireCodec.ReadPath(a, "p"), a.GetOptionalString("mt"), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteOpenFileResult(writer, result);
            }
            case RemoteOps.IntentEditFile:
            {
                AppIntentOpenFileResult result = await c.Intents.EditFileAsync(WireCodec.ReadPath(a, "p"), a.GetOptionalString("mt"), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteOpenFileResult(writer, result);
            }
            case RemoteOps.IntentReveal:
            {
                AppIntentResult result = await c.Intents.RevealFileAsync(WireCodec.ReadPath(a, "p"), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteIntentResult(writer, result);
            }
            case RemoteOps.IntentOpenUri:
            {
                AppIntentResult result = await c.Intents.OpenUriAsync(new Uri(a.GetString("u"), UriKind.Absolute), a.GetOptionalString("app"), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteIntentResult(writer, result);
            }
            case RemoteOps.IntentShowSettings:
            {
                AppIntentResult result = await c.Intents.ShowAppSettingsAsync(a.GetString("id"), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteIntentResult(writer, result);
            }
            case RemoteOps.IntentSend:
            {
                AppIntentResult result = await c.Intents.SendIntentAsync(
                    a.GetString("id"), a.GetOptionalString("pl"), a.GetOptionalString("app"),
                    a.TryGetProperty("rm", out _) && a.GetFlag("rm"), cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteIntentResult(writer, result);
            }
            case RemoteOps.ClipboardGetText:
            {
                string? text = await c.Clipboard.GetTextAsync(cancellationToken).ConfigureAwait(false);
                return writer =>
                {
                    if (text is not null)
                    {
                        writer.WriteString("tx", text);
                    }
                };
            }
            case RemoteOps.ClipboardSetText:
                await c.Clipboard.SetTextAsync(a.GetString("tx"), cancellationToken).ConfigureAwait(false);
                return static _ => { };
            case RemoteOps.ClipboardGetFiles:
            {
                var files = await c.Clipboard.GetFileDropListAsync(cancellationToken).ConfigureAwait(false);
                return writer => WireCodec.WriteClipboardFiles(writer, files);
            }
            case RemoteOps.ClipboardSetFiles:
                await c.Clipboard.SetFileDropListAsync(WireCodec.ReadClipboardFiles(a)!, cancellationToken).ConfigureAwait(false);
                return static _ => { };
            case RemoteOps.ClipboardClear:
                await c.Clipboard.ClearAsync(cancellationToken).ConfigureAwait(false);
                return static _ => { };
            case RemoteOps.EventSubscribe:
                Subscribe(a);
                return static _ => { };
            case RemoteOps.EventUnsubscribe:
                _events.Close(a.GetInt32("s"));
                return static _ => { };
            case RemoteOps.EventAcknowledge:
                _events.Acknowledge(a.GetInt32("s"), a.GetInt32("n"));
                return static _ => { };
            case RemoteOps.EventPublish:
            {
                TopicPublishResult result = PublishFromWorker(a);
                if (result.Outcome != TopicPublishOutcome.Delivered)
                {
                    // Publish is synchronous and provisional in the worker: the refusal goes to the app's log (ADR 0138, section 3).
                    c.Logging.Log(HackerOs.Simulation.Abstractions.Diagnostics.DiagnosticSeverity.Warning,
                        $"The OS refused a publication on '{a.GetString("top")}': {result.Outcome}.");
                }

                return static _ => { };
            }

            case RemoteOps.EventPublishWithResult:
            {
                // PublishAsync (ADR 0145): the app reads the outcome itself, nothing is logged.
                TopicPublishResult result = PublishFromWorker(a);
                return writer =>
                {
                    writer.WriteNumber("o", (int)result.Outcome);
                    if (result.SubscriberFaults.Count > 0)
                    {
                        writer.WriteStartArray("f");
                        foreach (EventDispatchFault fault in result.SubscriberFaults)
                        {
                            writer.WriteStartObject();
                            RemoteErrors.Write(writer, fault.Exception);
                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                    }
                };
            }
            case RemoteOps.EventSharedChannel:
                try
                {
                    c.Events.RegisterSharedChannel(WirePayloadCodec.ParseTopic(a.GetString("top")), Worker.RemoteEventGateway.ReadPolicy(a));
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                {
                    // The call is synchronous and returns nothing in the worker: the refusal goes to the app's log.
                    c.Logging.Log(HackerOs.Simulation.Abstractions.Diagnostics.DiagnosticSeverity.Warning,
                        $"The OS refused to register the shared channel '{a.GetString("top")}': {exception.Message}");
                }

                return static _ => { };
            case RemoteOps.WatchOpen:
            {
                int id = a.GetInt32("s");
                ITopicChannelSubscription<FileSystemChangeEvent> watch = await c.Watch.WatchAsync(
                    WireCodec.ReadPath(a, "p"), (FileSystemWatchScope)a.GetInt32("sc"), cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    await watch.DisposeAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                _events.OpenWatch(id, watch);
                return static _ => { };
            }
            case RemoteOps.WebDbOpen:
                await c.WebDatabase.OpenAsync(
                    JsonSerializer.Deserialize(a.GetString("j"), HackerOs.AppSdk.WebData.HackWebDbJsonContext.Default.HackWebDbSchemaDefinition)
                        ?? throw new ArgumentException("No schema.", "j"),
                    cancellationToken).ConfigureAwait(false);
                return static _ => { };
            case RemoteOps.WebDbExecute:
            {
                IReadOnlyList<string?> results = await c.WebDatabase.ExecuteAsync(
                    JsonSerializer.Deserialize(a.GetString("j"), HackerOs.AppSdk.WebData.HackWebDbJsonContext.Default.HackWebDbRequest)
                        ?? throw new ArgumentException("No request.", "j"),
                    cancellationToken).ConfigureAwait(false);
                string json = JsonSerializer.Serialize([.. results], HackerOs.AppSdk.WebData.HackWebDbJsonContext.Default.ListString);
                return writer => writer.WriteString("r", json);
            }
            case RemoteOps.FsDescriptorOpen:
                return OpenDescriptor(await c.FileSystem.OpenDescriptorAsync(WireCodec.ReadPath(a, "p"), cancellationToken).ConfigureAwait(false));
            case RemoteOps.FsDescriptorClose:
            {
                IDisposable? descriptor;
                lock (_gate)
                {
                    _descriptors.Remove(a.GetInt32("fd"), out descriptor);
                }

                descriptor?.Dispose();
                return static _ => { };
            }
            case RemoteOps.ProcessSetLimit:
            {
                ResourceLimitChangeResult changed = await c.Processes.SetResourceLimitAsync(
                    ProcessId.FromInt64(a.GetInt64("pid")), (ResourceLimitKind)a.GetInt32("k"),
                    new ResourceLimit(a.GetInt64("s"), a.GetInt64("h")), cancellationToken).ConfigureAwait(false);
                return writer => writer.WriteNumber("r", (int)changed);
            }
            default:
                throw new NotSupportedException($"Unknown remote operation '{operation}'.");
        }
    }

    /// <summary>Keeps a descriptor the OS opened for the process until the worker closes it or the process ends.</summary>
    private Action<Utf8JsonWriter> OpenDescriptor(FileSystemResult<IDisposable> result)
    {
        if (!result.Succeeded)
        {
            return writer => WireCodec.WriteResult(writer, FileSystemResult<int>.Failure(result.Error!), static (_, _) => { });
        }

        int id;
        bool closed;
        lock (_gate)
        {
            closed = _listenersClosed;
            id = ++_nextDescriptor;
            if (!closed)
            {
                _descriptors[id] = result.Value!;
            }
        }

        if (closed)
        {
            // The process ended while the descriptor was being opened.
            result.Value!.Dispose();
            throw new HackProcessKilledException(Pid);
        }

        return writer => WireCodec.WriteResult(writer, FileSystemResult<int>.Success(id), static (w, value) => w.WriteNumber("fd", value));
    }

    /// <summary>
    /// <c>ev.sub</c> (ADR 0124, section 1): a real subscription of the OS's bus, made by the process's
    /// own trusted gateway (its identity, its rights), whose deliveries are queued for the worker.
    /// </summary>
    private void Subscribe(JsonElement a)
    {
        AppEventGateway events = TrustedEvents();
        int id = a.GetInt32("s");
        string wireName = a.GetString("w");
        if (a.GetString("k") == Worker.RemoteEventGateway.EventKind)
        {
            _events.Open(id, boundedCapacity: null, enqueue =>
                events.SubscribeEventWire(wireName, (type, @event) => enqueue(_events.EventMessage(id, type, @event))));
            return;
        }

        TopicName topic = WirePayloadCodec.ParseTopic(a.GetString("top"));
        int? capacity = a.GetOptional("c")?.GetInt32();
        _events.Open(id, capacity, enqueue =>
            events.SubscribeWire(topic, wireName, delivery => enqueue(_events.TopicMessage(id, delivery))));
    }

    /// <summary>
    /// <c>ev.pub</c>: publishes what the process serialized in its worker, with its identity. Nobody
    /// waits for the answer in the worker (ADR 0138), so a refusal goes to the app's log, where the
    /// developer looks when a message never arrives.
    /// </summary>
    /// <summary>Publishes a worker's message on the OS's bus with the process's own identity (<c>ev.pub</c>, <c>ev.pubr</c>).</summary>
    private TopicPublishResult PublishFromWorker(JsonElement a)
    {
        string json = a.GetString("j");
        return TrustedEvents().PublishWire(
            WirePayloadCodec.ParseTopic(a.GetString("top")), a.GetString("w"), json, type => WirePayloadCodec.Deserialize(type, json));
    }

    private AppEventGateway TrustedEvents() =>
        _context.Events as AppEventGateway
        ?? throw new NotSupportedException("This process's event gateway cannot serve a worker.");

    private static Action<Utf8JsonWriter> Mutation(FileSystemMutationResult result) => writer => WireCodec.WriteMutation(writer, result);

    private static Action<Utf8JsonWriter> ServiceResult(ServiceControlResult result) => writer => WireCodec.WriteServiceResult(writer, result);

    /// <summary>
    /// <c>open(2)</c> + first <c>read(2)</c>: the whole content of a regular file up to
    /// <see cref="InlineReadLimit"/>, otherwise the first chunk and a handle for the rest.
    /// </summary>
    private async Task<Action<Utf8JsonWriter>> ReadAsync(FileSystemReadRequest request, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemContentReadHandle> result = await _context.FileSystem.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            FileSystemError error = result.Error!;
            return writer =>
            {
                writer.WriteBoolean("ok", false);
                WireCodec.WriteError(writer, "err", error);
            };
        }

        FileSystemContentReadHandle handle = result.Value!;
        int limit = handle.Entry.Metadata is FileMetadata { Length: <= InlineReadLimit } file
            ? (int)Math.Max(file.Length + 1, 1)
            : ChunkSize;
        (byte[] bytes, bool endOfStream) = await ReadUpToAsync(handle.Content, limit, untilEnd: handle.Entry.Metadata is FileMetadata { Length: <= InlineReadLimit }, cancellationToken).ConfigureAwait(false);
        int? id = null;
        if (endOfStream)
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            lock (_gate)
            {
                id = ++_nextReadHandle;
                _reads[id.Value] = handle;
            }
        }

        return writer =>
        {
            writer.WriteBoolean("ok", true);
            WireCodec.WriteSnapshot(writer, "en", handle.Entry);
            WireCodec.WriteDescriptor(writer, "d", handle.Descriptor);
            writer.WriteBase64String("b", bytes);
            writer.WriteBoolean("eof", endOfStream);
            if (id is { } value)
            {
                writer.WriteNumber("h", value);
            }
        };
    }

    private async Task<Action<Utf8JsonWriter>> ReadChunkAsync(int id, int count, CancellationToken cancellationToken)
    {
        FileSystemContentReadHandle? handle;
        lock (_gate)
        {
            _reads.TryGetValue(id, out handle);
        }

        if (handle is null)
        {
            return static writer =>
            {
                writer.WriteBase64String("b", ReadOnlySpan<byte>.Empty);
                writer.WriteBoolean("eof", true);
            };
        }

        (byte[] bytes, bool endOfStream) = await ReadUpToAsync(handle.Content, Math.Clamp(count, 1, 4 * ChunkSize), untilEnd: false, cancellationToken).ConfigureAwait(false);
        if (endOfStream)
        {
            CloseRead(id);
        }

        return writer =>
        {
            writer.WriteBase64String("b", bytes);
            writer.WriteBoolean("eof", endOfStream);
        };
    }

    private void CloseRead(int id)
    {
        FileSystemContentReadHandle? handle;
        lock (_gate)
        {
            _reads.Remove(id, out handle);
        }

        if (handle is not null)
        {
            _ = handle.DisposeAsync();
        }
    }

    /// <summary>
    /// Reads at most <paramref name="limit"/> bytes. With <paramref name="untilEnd"/>, keeps reading
    /// to the end of a file whose size is known (its length plus one byte proves the end).
    /// </summary>
    private static async Task<(byte[] Bytes, bool EndOfStream)> ReadUpToAsync(Stream content, int limit, bool untilEnd, CancellationToken cancellationToken)
    {
        using MemoryStream copy = new();
        byte[] buffer = new byte[Math.Min(limit, ChunkSize)];
        while (true)
        {
            int wanted = untilEnd ? buffer.Length : (int)Math.Min(buffer.Length, limit - copy.Length);
            if (wanted <= 0)
            {
                return (copy.ToArray(), false);
            }

            int read = await content.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return (copy.ToArray(), true);
            }

            copy.Write(buffer, 0, read);
            if (!untilEnd && copy.Length >= limit)
            {
                return (copy.ToArray(), false);
            }
        }
    }

    /// <summary>The bytes a worker process wrote, as the content source of a write.</summary>
    private sealed class BytesContentSource(FileSystemContentDescriptor descriptor, byte[] content) : IFileSystemContentSource
    {
        public FileSystemContentDescriptor Descriptor => descriptor;

        public long? Length => content.Length;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
    }
}
