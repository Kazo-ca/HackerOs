using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Diagnostics;
using HackerOs.AppSdk.Threading;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.Processes;
using HackerOs.Platform.WorkerHost.Protocol;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.WorkerHost.Worker;

/// <summary>
/// The OS inside one Web Worker (ADR 0070, section 4): it runs the processes the OS places here, each
/// with a remote <see cref="IAppExecutionContext"/>, on this worker's single thread; it delivers their
/// stop requests and kills (ADR 0069), and sends a heartbeat every 250 ms with their CPU time and the
/// runtime's memory (section 5). A heartbeat that stops is how the OS sees this thread is stuck.
/// </summary>
/// <remarks>
/// Messages arrive through <see cref="Receive"/> and leave through the <c>post</c> delegate. The host
/// never blocks: every wait is asynchronous, so one process awaiting the OS lets the others run.
/// </remarks>
public sealed class WorkerProcessHost
{
    private readonly Action<string> _post;
    private readonly IWorkerAssemblyLoader _loader;
    private readonly bool _canMeterCpu;
    private readonly WorkerRpcClient _rpc;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, WorkerProcess> _processes = [];
    private HashSet<string> _knownAssemblies = new(StringComparer.Ordinal);
    private WorkerClock? _clock;
    private WorkerCpuMeter? _meter;
    private TimeSpan _heartbeatPeriod = TimeSpan.FromMilliseconds(250);
    private int _workerId;
    private long _heartbeatSequence;
    private bool _initialized;
    private volatile bool _stopped;

    /// <summary>Initializes the worker's host.</summary>
    /// <param name="post">Posts one message to the OS (<c>self.postMessage</c> in a real worker).</param>
    /// <param name="loader">Loads the apps' assemblies.</param>
    /// <param name="canMeterCpu">
    /// Whether this runtime belongs to the worker alone, so the context-switch hook may be installed
    /// (<see langword="false"/> for the in-process loopback, which shares the OS's statics).
    /// </param>
    public WorkerProcessHost(Action<string> post, IWorkerAssemblyLoader loader, bool canMeterCpu)
        : this(post, loader, canMeterCpu, null)
    {
    }

    /// <summary>Initializes the worker's host, with synchronous calls to the OS when the page allows them.</summary>
    /// <param name="post">Posts one message to the OS (<c>self.postMessage</c> in a real worker).</param>
    /// <param name="loader">Loads the apps' assemblies.</param>
    /// <param name="canMeterCpu">Whether the context-switch hook may be installed (see the other constructor).</param>
    /// <param name="callSync">
    /// Waits for the OS's answer to one call (ADR 0131, section 4: <c>Atomics.wait</c> on a cross-origin
    /// isolated page), or <see langword="null"/>: the synchronous members of the gateways are then refused.
    /// </param>
    public WorkerProcessHost(Action<string> post, IWorkerAssemblyLoader loader, bool canMeterCpu, WorkerSynchronousCall? callSync)
    {
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _canMeterCpu = canMeterCpu;
        _rpc = new WorkerRpcClient(post, callSync);
    }

    /// <summary>Gets the number of processes running here.</summary>
    public int ProcessCount
    {
        get
        {
            lock (_gate)
            {
                return _processes.Count;
            }
        }
    }

    /// <summary>Handles one message from the OS.</summary>
    /// <param name="message">The JSON text.</param>
    public void Receive(string message)
    {
        if (_stopped)
        {
            return;
        }

        try
        {
            using JsonDocument document = WorkerMessage.Parse(message, out string type);
            JsonElement root = document.RootElement;
            switch (type)
            {
                case WorkerMessageType.Init:
                    Initialize(root);
                    break;
                case WorkerMessageType.Start:
                    Start(root);
                    break;
                case WorkerMessageType.Result:
                    _rpc.Complete(root);
                    break;
                case WorkerMessageType.Stop:
                    Find(root)?.DeliverStop(
                        (ProcessExitReason)root.GetInt32("r"), WireCodec.ReadTime(root, "ra"), WireCodec.ReadTime(root, "dl"));
                    break;
                case WorkerMessageType.Interrupt:
                    Find(root)?.Interrupt();
                    break;
                case WorkerMessageType.Kill:
                    Kill(root.GetInt64("p"));
                    break;
                case WorkerMessageType.Intent:
                    Find(root)?.Context.DeliverIntent(WireCodec.ReadIntentDelivery(root.GetProperty("it")));
                    break;
                case WorkerMessageType.ServiceStop:
                    Find(root)?.StopService((ServiceStopReason)root.GetInt32("r"));
                    break;
                case WorkerMessageType.Clock:
                    _clock?.Update(WireCodec.ReadTime(root, "u"), root.GetInt64("k"));
                    break;
                case WorkerMessageType.PermissionDenied:
                    Find(root)?.Context.RaisePermissionDenied(WireCodec.ReadError(root.GetProperty("err")));
                    break;
                case WorkerMessageType.OutputAck:
                    Find(root)?.Output.Acknowledge(root.GetInt64("n"));
                    break;
                case WorkerMessageType.PipeClosed:
                    Find(root)?.Output.Close(root.GetInt32("f"));
                    break;
                case WorkerMessageType.Event:
                    Find(root)?.Context.EventTable.Deliver(root.GetInt32("s"), root);
                    break;
                case WorkerMessageType.BackgroundJobProgress:
                    Find(root)?.Context.BackgroundJobs.Deliver(
                        root.GetInt32("j"), new HackBackgroundProgress(root.GetDouble("f"), root.GetOptionalString("m")));
                    break;
                case WorkerMessageType.UiInput:
                    Find(root)?.Window?.Deliver(root.GetProperty("d").Clone());
                    break;
                case WorkerMessageType.HttpRequest:
                    Find(root)?.Channel.Listeners.Deliver(root.GetInt32("sk"), WireCodec.ReadListenerExchange(root.GetProperty("ex")));
                    break;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log($"worker {_workerId}: bad message: {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>Stops the heartbeat (the loopback's terminate; a real worker simply dies).</summary>
    public void Shutdown() => _stopped = true;

    private void Initialize(JsonElement root)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _workerId = root.GetInt32("w");
        _heartbeatPeriod = TimeSpan.FromMilliseconds(Math.Max(10, root.GetInt32("hb")));
        if (_canMeterCpu && root.GetFlag("cpu") && !HackExecutionContext.IsContextSwitchHookInstalled)
        {
            _meter = new WorkerCpuMeter();
            HackExecutionContext.InstallContextSwitchHook(_meter);
        }

        if (root.TryGetProperty("known", out _))
        {
            _knownAssemblies = new HashSet<string>(WireCodec.ReadStrings(root, "known"), StringComparer.Ordinal);
        }

        _ = WarmUpAsync(WireCodec.ReadStrings(root, "pre"));
    }

    /// <summary>Loads the preloaded assemblies, says hello, then starts the heartbeat.</summary>
    private async Task WarmUpAsync(IReadOnlyList<string> preload)
    {
        try
        {
            await _loader.PreloadAsync(preload).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A missing assembly only delays the first process that needs it.
            Log($"worker {_workerId}: preload failed: {exception.Message}");
        }

        _post(WorkerMessage.Create(WorkerMessageType.Hello, writer => writer.WriteNumber("w", _workerId)));
        await HeartbeatAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Every period, while this thread is free: the times of each process and the runtime's memory.
    /// A process stuck in a loop keeps this thread, so the heartbeat stops with it.
    /// </summary>
    private async Task HeartbeatAsync()
    {
        while (!_stopped)
        {
            await Task.Delay(_heartbeatPeriod).ConfigureAwait(true);
            if (_stopped)
            {
                return;
            }

            SendHeartbeat();
        }
    }

    private void SendHeartbeat()
    {
        List<WorkerProcess> processes;
        lock (_gate)
        {
            processes = [.. _processes.Values];
        }

        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        _post(WorkerMessage.Create(WorkerMessageType.Heartbeat, writer =>
        {
            writer.WriteNumber("w", _workerId);
            writer.WriteNumber("sq", ++_heartbeatSequence);
            writer.WriteNumber("heap", GC.GetTotalMemory(forceFullCollection: false));
            writer.WriteNumber("comm", memory.TotalCommittedBytes);
            // The whole runtime: in a browser, the worker's WebAssembly memory (managed heap, runtime, code).
            writer.WriteNumber("rss", Environment.WorkingSet);
            if (_meter is { } meter)
            {
                writer.WriteNumber("busy", meter.BusyTime.Ticks);
            }

            writer.WriteStartArray("ps");
            foreach (WorkerProcess process in processes)
            {
                writer.WriteStartObject();
                writer.WriteNumber("p", process.Pid);
                if (process.RunningFor is { } running)
                {
                    // How long its code has had this thread: the OS reads a heartbeat sent a period
                    // after that as proof the process yields (ADR 0072, section 6).
                    writer.WriteNumber("rn", running.Ticks);
                }

                if (_meter?.Get(process.Pid) is { } times)
                {
                    writer.WriteNumber("ut", times.UserTime.Ticks);
                    writer.WriteNumber("st", times.SystemTime.Ticks);
                    writer.WriteNumber("lt", times.LongTasks);
                    writer.WriteNumber("ls", times.LongestSlice.Ticks);
                    writer.WriteNumber("cs", times.Slices);
                    writer.WriteNumber("al", times.AllocatedBytes);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }));
    }

    private void Start(JsonElement root)
    {
        long pid = root.GetInt64("p");
        JsonElement identity = root.GetProperty("id");
        JsonElement environment = root.GetProperty("env");
        _clock ??= new WorkerClock(WireCodec.ReadTime(root, "cu"), root.GetInt64("ck"));
        _clock.Update(WireCodec.ReadTime(root, "cu"), root.GetInt64("ck"));
        AppManifest manifest = AppManifestJsonSerializer.NormalizeOptionalCollections(
            AppManifestJsonSerializer.DeserializeStrict(root.GetString("mf")));
        RemoteProcessSnapshot snapshot = new(
            manifest,
            Guid.ParseExact(identity.GetString("in"), "N"),
            identity.GetString("u"),
            identity.GetString("home"),
            identity.GetFlag("root"),
            identity.GetString("login"),
            identity.GetString("disp"),
            (AppAuthority)identity.GetInt32("auth"),
            new HashSet<string>(WireCodec.ReadStrings(identity, "caps"), StringComparer.Ordinal),
            SessionId.FromGuid(Guid.ParseExact(identity.GetString("s"), "N")),
            WireCodec.ReadProcess(identity.GetProperty("pr")),
            WireCodec.ReadStrings(environment, "argv"),
            environment.GetString("cwd"),
            WireCodec.ReadDictionary(environment, "vars"),
            environment.GetString("user"),
            environment.GetString("host"),
            identity.GetOptional("it") is { } intent ? WireCodec.ReadIntentDelivery(intent) : null);

        WorkerProcess process = new(this, pid, snapshot, root.GetFlag("in"), _clock);
        lock (_gate)
        {
            _processes[pid] = process;
        }

        if (root.GetInt32("k") == JobKind)
        {
            // ADR 0128: a background task, named by its target, with its input as JSON.
            JsonElement target = root.GetProperty("jt");
            _ = RunJobAsync(process, new HackBackgroundJobTarget(
                target.GetString("asm"), target.GetString("ty"), target.GetOptionalString("me"), target.GetString("it")),
                root.GetString("ji"));
            return;
        }

        if (root.GetInt32("k") == WindowKind)
        {
            // ADR 0129 trial: a window app rendered here, its DOM on the page (ADR 0133).
            _ = RunWindowAsync(process, root.GetString("asm"), root.GetString("ty"), root.GetString("ra"), root.GetString("rt"));
            return;
        }

        StartInfo start = new(
            root.GetInt32("k") == 1 ? AppKind.Service : AppKind.Terminal,
            root.GetString("asm"),
            root.GetString("ty"),
            WireCodec.ReadStrings(root, "argv"),
            root.GetString("cwd"),
            WireCodec.ReadDictionary(root, "envs"));
        _ = RunAsync(process, start);
    }

    private async Task RunAsync(WorkerProcess process, StartInfo start)
    {
        RemoteProcessExit exit;
        try
        {
            EnsureKnownAssembly(start.Assembly);
            Type entryType = await _loader.LoadEntryTypeAsync(start.Assembly, start.TypeName).ConfigureAwait(true);
            object instance = CreateInstance(entryType, process.Context.Manifest);
            exit = start.Kind == AppKind.Service
                ? await process.RunServiceAsync((ServiceAppBase)instance).ConfigureAwait(true)
                : await process.RunTerminalAsync((TerminalAppBase)instance, start).ConfigureAwait(true);
        }
        catch (TypeLoadException exception)
        {
            // ADR 0124, section 4: an assembly the worker cannot load is a launch failure, like a
            // command the shell cannot find (exit code 127), not a crash of the program.
            process.Output.StandardError.WriteLine($"{start.Assembly}: {exception.Message}");
            exit = RemoteProcessExit.Exited(LaunchFailedExitCode);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The message alone, as the in-process launch reports it (lifecycle.entry-point.fault).
            exit = new RemoteProcessExit(RemoteProcessExitStatus.Faulted, FaultMessage: exception.Message);
        }

        End(process, exit);
    }

    /// <summary><c>k</c> of a <c>start</c> message for a background task (ADR 0128): 0 command, 1 service, 2 task.</summary>
    internal const int JobKind = 2;

    /// <summary><c>k</c> of a <c>start</c> message for a window rendered in the worker (ADR 0129 trial, ADR 0133).</summary>
    internal const int WindowKind = 3;

    /// <summary>
    /// Runs a window app (ADR 0129 trial): loads the app's assembly and the runner the OS named, then
    /// lets the runner render the window component until it returns. A window that faults ends like a
    /// faulted command; the OS closes the window.
    /// </summary>
    private async Task RunWindowAsync(WorkerProcess process, string assembly, string typeName, string runnerAssembly, string runnerType)
    {
        RemoteProcessExit exit;
        try
        {
            EnsureKnownAssembly(assembly);
            Type entryType = await _loader.LoadEntryTypeAsync(assembly, typeName).ConfigureAwait(true);
            // The runner is platform code the OS names (part of the page's boot set), not an app: it is
            // not checked against the apps known to the build, and loads by name.
            Type runner = await _loader.LoadEntryTypeAsync(runnerAssembly, runnerType).ConfigureAwait(true);
            exit = await process.RunWindowAsync(CreateRunner(runner), entryType).ConfigureAwait(true);
        }
        catch (TypeLoadException exception)
        {
            process.Output.StandardError.WriteLine($"{assembly}: {exception.Message}");
            exit = RemoteProcessExit.Exited(LaunchFailedExitCode);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            exit = new RemoteProcessExit(RemoteProcessExitStatus.Faulted, FaultMessage: exception.Message);
        }

        End(process, exit);
    }

    /// <summary>Creates the window runner the OS named (a type of the page's own rendering library).</summary>
    [UnconditionalSuppressMessage(
        "Trimming", "IL2067",
        Justification = "The runner type is HackerOs.Platform.RemoteRendering's WorkerWindowRunner, part of the host's " +
            "boot set; its parameterless constructor is rooted there with DynamicDependency (ADR 0133).")]
    private static IWorkerWindowRunner CreateRunner(Type runnerType) =>
        Activator.CreateInstance(runnerType) as IWorkerWindowRunner
        ?? throw new InvalidOperationException($"'{runnerType.FullName}' is not an {nameof(IWorkerWindowRunner)}.");

    /// <summary>
    /// Runs a background task (ADR 0128, section 3): loads the app's assembly, finds the task, reads its
    /// input, runs it on the process's scheduler, and reports its result, its fault or its refusal in
    /// the <c>exit</c> message. Progress reports travel as <c>bg.progress</c> notes.
    /// </summary>
    private async Task RunJobAsync(WorkerProcess process, HackBackgroundJobTarget target, string inputJson)
    {
        RemoteProcessExit exit;
        try
        {
            EnsureKnownAssembly(target.AssemblyName + ".dll");
            Type type = await _loader.LoadEntryTypeAsync(target.AssemblyName + ".dll", target.TypeName).ConfigureAwait(true);
            HackBackgroundJobRunner.PreparedJob job = HackBackgroundJobRunner.Prepare(type.Assembly, target, inputJson);
            exit = await process.RunJobAsync(job).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is ArgumentException or TypeLoadException)
        {
            // Nothing valid to run, like exec(2) failing: the caller sees an ArgumentException.
            exit = new RemoteProcessExit(RemoteProcessExitStatus.Faulted, LaunchFailedExitCode, exception.Message)
            {
                JobRejected = true,
                JobFault = new HackBackgroundJobException(exception.GetType().FullName!, exception.Message, exception.StackTrace)
            };
        }

        End(process, exit);
    }

    /// <summary>Exit code of a process whose assembly the worker refused or could not load (ADR 0124, section 4).</summary>
    internal const int LaunchFailedExitCode = 127;

    /// <summary>
    /// ADR 0124, section 4: a worker loads any assembly known to the build, or one already part of its
    /// runtime (the boot set); anything else is refused before a byte of it is fetched.
    /// </summary>
    /// <exception cref="TypeLoadException">The assembly is not known to this build.</exception>
    private void EnsureKnownAssembly(string assemblyFile)
    {
        if (!IsLoadable(_knownAssemblies, assemblyFile, static name =>
            AppDomain.CurrentDomain.GetAssemblies().Any(assembly => string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal))))
        {
            throw new TypeLoadException($"'{assemblyFile}' is not an app assembly known to this build.");
        }
    }

    /// <summary>The rule of <see cref="EnsureKnownAssembly"/>: no list, in the list, or already in the runtime.</summary>
    /// <param name="known">The build's app assemblies; empty when no list is enforced.</param>
    /// <param name="assemblyFile">The file name the OS asks for (<c>HackerOs.Commands.dll</c>).</param>
    /// <param name="isInRuntime">Whether an assembly of that simple name is already loaded.</param>
    internal static bool IsLoadable(IReadOnlySet<string> known, string assemblyFile, Func<string, bool> isInRuntime) =>
        known.Count == 0
        || known.Contains(assemblyFile)
        || isInRuntime(Path.GetFileNameWithoutExtension(assemblyFile));

    /// <summary>
    /// Creates the app's entry point with its manifest, as the OS does in-process (ADR 0055: an app's
    /// constructor takes its manifest only).
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming", "IL2067",
        Justification = "The entry-point type was resolved and validated by the OS's discovery against the host's " +
            "explicit app allowlist; a trimmed host keeps that assembly whole (ADR 0113: generated root descriptor).")]
    private static object CreateInstance(Type entryType, AppManifest manifest) =>
        Activator.CreateInstance(entryType, manifest)
        ?? throw new InvalidOperationException($"Could not create '{entryType.FullName}'.");

    private void End(WorkerProcess process, RemoteProcessExit exit)
    {
        process.Channel.Listeners.CloseAll();
        process.Context.EventTable.Close();
        process.Output.Flush();
        process.Output.Abort();
        lock (_gate)
        {
            _processes.Remove(process.Pid);
        }

        _meter?.Forget(process.Pid);
        _post(WorkerMessage.Create(WorkerMessageType.Exit, writer =>
        {
            writer.WriteNumber("p", process.Pid);
            writer.WriteNumber("s", (int)exit.Status);
            writer.WriteNumber("c", exit.ExitCode);
            if (exit.FaultMessage is { } message)
            {
                writer.WriteString("m", message);
            }

            // ADR 0128: what a background task returned, threw, or why it was refused.
            if (exit.JobResult is { } result)
            {
                writer.WriteString("jr", result);
            }

            if (exit.JobFault is { } fault)
            {
                writer.WriteStartObject("jf");
                writer.WriteString("ty", fault.OriginalTypeName);
                writer.WriteString("m", fault.Message);
                if (fault.OriginalStackTrace is { } stack)
                {
                    writer.WriteString("st", stack);
                }

                writer.WriteEndObject();
            }

            if (exit.JobRejected)
            {
                writer.WriteBoolean("jx", true);
            }
        }));
    }

    /// <summary>
    /// <c>SIGKILL</c> while this thread still answers (escalation step 3): the process's pending
    /// continuations are dropped and its pending calls fail; the worker and its other processes live on.
    /// </summary>
    private void Kill(long pid)
    {
        WorkerProcess? process;
        lock (_gate)
        {
            _processes.TryGetValue(pid, out process);
        }

        process?.Kill();
        _rpc.FailProcess(pid, new HackProcessKilledException(pid));
        _post(WorkerMessage.Create(WorkerMessageType.KillAck, writer => writer.WriteNumber("p", pid)));
    }

    private WorkerProcess? Find(JsonElement root)
    {
        long pid = root.GetInt64("p");
        lock (_gate)
        {
            return _processes.GetValueOrDefault(pid);
        }
    }

    private void Log(string line)
    {
        try
        {
            _post(WorkerMessage.Create(WorkerMessageType.Log, writer => writer.WriteString("m", line)));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Nothing else to tell.
        }
    }

    internal WorkerRpcClient Rpc => _rpc;

    internal void Post(string message) => _post(message);

    /// <summary>What a <c>start</c> message asks to run.</summary>
    internal sealed record StartInfo(
        AppKind Kind,
        string Assembly,
        string TypeName,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        IReadOnlyDictionary<string, string> Environment);
}

/// <summary>One process running in a worker: its remote context, streams, control block and token.</summary>
internal sealed class WorkerProcess
{
    private readonly WorkerProcessHost _host;
    private readonly CancellationTokenSource _token = new();
    private ServiceAppBase? _service;
    private long _entered;

    public WorkerProcess(WorkerProcessHost host, long pid, RemoteProcessSnapshot snapshot, bool inputAttached, WorkerClock clock)
    {
        _host = host;
        Pid = pid;
        Control = new ProcessControlBlock(ProcessId.FromInt64(pid));
        Output = new RemoteProcessOutput(pid, host.Post);
        RemoteProcessChannel channel = new(pid, host.Rpc, Output, Control, host.Post);
        Channel = channel;
        Input = inputAttached
            ? new RemoteStandardInput(async (count, cancellationToken) => StandardInputWire.ReadChunk(
                await channel.CallAsync(RemoteOps.StdinRead, writer => writer.WriteNumber("n", count), cancellationToken).ConfigureAwait(false)))
            : new StringReader(string.Empty);
        Context = new RemoteAppExecutionContext(
            snapshot, channel, clock,
            new RemoteConsoleGateway(true, Input, Output.StandardOutput, Output.StandardError),
            _token.Token);
    }

    public long Pid { get; }

    public ProcessControlBlock Control { get; }

    public RemoteProcessOutput Output { get; }

    /// <summary>Gets the process's line to the OS, with its listening sockets.</summary>
    public RemoteProcessChannel Channel { get; }

    public TextReader Input { get; }

    public RemoteAppExecutionContext Context { get; }

    /// <summary>
    /// Gets how long ago the process's own code was first given this thread, or <see langword="null"/>
    /// while it is still being loaded (its assembly load yields before any of its code runs).
    /// </summary>
    public TimeSpan? RunningFor
    {
        get
        {
            long entered = Interlocked.Read(ref _entered);
            return entered == 0 ? null : Stopwatch.GetElapsedTime(entered);
        }
    }

    /// <summary>Notes that the process's own code runs now (called on its scheduler, just before its entry point).</summary>
    private void MarkEntered() => Interlocked.CompareExchange(ref _entered, Stopwatch.GetTimestamp(), 0);

    /// <summary>Runs a command, mapping its ending like the OS does in-process.</summary>
    public async Task<RemoteProcessExit> RunTerminalAsync(TerminalAppBase app, WorkerProcessHost.StartInfo start)
    {
        TerminalExecutionContext terminal = new(
            Context, start.Arguments, Input, Output.StandardOutput, Output.StandardError,
            start.WorkingDirectory, start.Environment);
        try
        {
            using IDisposable ambient = HackExecutionContext.Enter(Context);
            int code = await Control.RunAsync(() =>
            {
                MarkEntered();
                return app.ExecuteAsync(terminal, _token.Token).AsTask();
            }).ConfigureAwait(true);
            return RemoteProcessExit.Exited(code);
        }
        catch (HackProcessKilledException)
        {
            return new RemoteProcessExit(RemoteProcessExitStatus.Killed);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested || Context.StopRequest is not null)
        {
            return new RemoteProcessExit(RemoteProcessExitStatus.Cancelled);
        }
        catch (RemoteBrokenPipeException)
        {
            return new RemoteProcessExit(RemoteProcessExitStatus.BrokenPipe);
        }
    }

    /// <summary>
    /// Runs a background task (ADR 0128) on the process's scheduler, in its ambient context: its
    /// token is the process token (cancelled by the caller's <c>int</c> or a stop request), its reports
    /// go to the OS as <c>bg.progress</c> notes.
    /// </summary>
    public async Task<RemoteProcessExit> RunJobAsync(HackBackgroundJobRunner.PreparedJob job)
    {
        HackBackgroundContext context = new(Pid, _token.Token, progress => Channel.Notify(RemoteOps.BackgroundProgress, writer =>
        {
            writer.WriteNumber("f", progress.Fraction);
            if (progress.Message is { } message)
            {
                writer.WriteString("m", message);
            }
        }));
        try
        {
            using IDisposable ambient = HackExecutionContext.Enter(Context);
            string? result = await Control.RunAsync(() =>
            {
                MarkEntered();
                return HackBackgroundFaults.RunAsync(job, context);
            }).ConfigureAwait(true);
            return new RemoteProcessExit(RemoteProcessExitStatus.Exited) { JobResult = result };
        }
        catch (HackProcessKilledException)
        {
            return new RemoteProcessExit(RemoteProcessExitStatus.Killed);
        }
        catch (OperationCanceledException)
        {
            return new RemoteProcessExit(RemoteProcessExitStatus.Cancelled);
        }
        catch (HackBackgroundJobException fault)
        {
            return new RemoteProcessExit(RemoteProcessExitStatus.Faulted, 1, fault.Message) { JobFault = fault };
        }
    }

    /// <summary>Gets the window rendered by this process (ADR 0129 trial), or <see langword="null"/>.</summary>
    public WorkerWindowRun? Window { get; private set; }

    /// <summary>
    /// Runs a window (ADR 0129 trial) on the process's scheduler, in its ambient context, until its
    /// runner returns: the OS closed it, a stop request was honored, or rendering failed.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming", "IL2067",
        Justification = "The entry-point type was resolved and validated by the OS's discovery against the host's " +
            "explicit app allowlist; a trimmed host keeps that assembly whole (ADR 0113: generated root descriptor).")]
    public async Task<RemoteProcessExit> RunWindowAsync(IWorkerWindowRunner runner, Type entryType)
    {
        Window = new WorkerWindowRun(Pid, Context, entryType, _host.Post);
        try
        {
            using IDisposable ambient = HackExecutionContext.Enter(Context);
            int code = await Control.RunAsync(() =>
            {
                MarkEntered();
                return runner.RunAsync(Window, _token.Token);
            }).ConfigureAwait(true);
            return RemoteProcessExit.Exited(code);
        }
        catch (HackProcessKilledException)
        {
            return new RemoteProcessExit(RemoteProcessExitStatus.Killed);
        }
        catch (OperationCanceledException)
        {
            return new RemoteProcessExit(RemoteProcessExitStatus.Cancelled);
        }
    }

    /// <summary>Runs a service until it returns or honors its token.</summary>
    public async Task<RemoteProcessExit> RunServiceAsync(ServiceAppBase service)
    {
        _service = service;
        try
        {
            await Control.RunAsync(async () =>
            {
                MarkEntered();
                await service.RunAsync(Context, Context.CancellationToken).ConfigureAwait(true);
                return 0;
            }).ConfigureAwait(true);
            return RemoteProcessExit.Exited(0);
        }
        catch (HackProcessKilledException)
        {
            return new RemoteProcessExit(RemoteProcessExitStatus.Killed);
        }
        catch (OperationCanceledException)
        {
            return new RemoteProcessExit(RemoteProcessExitStatus.Cancelled);
        }
    }

    /// <summary>
    /// <c>SIGTERM</c> (ADR 0069, section 1): raise <c>StopRequested</c> once, tell the OS when the work
    /// the app deferred is done, then cancel the process token.
    /// </summary>
    public void DeliverStop(ProcessExitReason reason, DateTimeOffset requestedAt, DateTimeOffset deadline)
    {
        if (Context.DeliverStopRequest(reason, requestedAt, deadline) is not { } request)
        {
            return;
        }

        _ = ReportStopWorkAsync(request);
        Interrupt();
    }

    /// <summary>Cancels the process token (Ctrl+C, or after the stop request).</summary>
    public void Interrupt()
    {
        try
        {
            _token.Cancel();
        }
        catch (AggregateException)
        {
            // A callback of the app threw; the token is cancelled anyway.
        }
    }

    /// <summary>Runs the service's <c>OnStoppingAsync</c> (logout, shutdown, disable) and reports it.</summary>
    public void StopService(ServiceStopReason reason) => _ = StopServiceAsync(reason);

    public void Kill()
    {
        Control.MarkKilled();
        Context.EventTable.Close();
        Output.Abort();
        Interrupt();
    }

    private async Task ReportStopWorkAsync(HackStopRequestedEventArgs request)
    {
        try
        {
            await request.WhenDeferredWorkEndedAsync().ConfigureAwait(true);
        }
        finally
        {
            _host.Post(WorkerMessage.Create(WorkerMessageType.StopDone, writer => writer.WriteNumber("p", Pid)));
        }
    }

    private async Task StopServiceAsync(ServiceStopReason reason)
    {
        try
        {
            if (_service is { } service)
            {
                using CancellationTokenSource budget = new(TimeSpan.FromSeconds(5));
                await service.StopAsync(reason, budget.Token).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Best effort, like a local service: the OS ends the process anyway.
        }
        finally
        {
            _host.Post(WorkerMessage.Create(WorkerMessageType.ServiceStopped, writer => writer.WriteNumber("p", Pid)));
        }
    }
}
