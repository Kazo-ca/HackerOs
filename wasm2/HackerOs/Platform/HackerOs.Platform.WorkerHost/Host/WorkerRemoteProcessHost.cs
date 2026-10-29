using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Diagnostics;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.WorkerHost.Protocol;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.Platform.WorkerHost.Host;

/// <summary>
/// Runs commands and services in Web Workers (ADR 0070): the <see cref="IRemoteProcessHost"/> the
/// WebAssembly hosts give the orchestrator. A command goes to a pool worker (its pipeline's, if it is
/// a stage); a <c>Dedicated</c> process to a worker of its own; a web app to the web worker every
/// site shares (ADR 0074).
/// </summary>
public sealed class WorkerRemoteProcessHost : IRemoteProcessHost
{
    private readonly WorkerPool _pool;

    /// <summary>Initializes the host over the pool.</summary>
    public WorkerRemoteProcessHost(WorkerPool pool) => _pool = pool ?? throw new ArgumentNullException(nameof(pool));

    /// <inheritdoc />
    public bool ShouldRunRemotely(AppManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Kind is AppKind.Terminal or AppKind.Service or AppKind.Web
            && AppExecutionHosts.IsWorker(AppExecutionHosts.Of(manifest));
    }

    /// <inheritdoc />
    public async Task<RemoteProcessExit> RunTerminalAsync(
        RemoteProcessLaunch launch, TerminalExecutionContext terminal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(terminal);
        bool dedicated = AppExecutionHosts.Of(launch.Descriptor.Manifest) == AppExecutionHost.Dedicated;
        Guid? group = dedicated ? null : launch.PlacementGroup;
        WorkerConnection worker = dedicated
            ? await _pool.AcquireDedicatedAsync(cancellationToken).ConfigureAwait(false)
            : await _pool.AcquireForCommandAsync(group, _pool.NiceOf(launch.Context.ProcessId), cancellationToken).ConfigureAwait(false);
        try
        {
            RemoteProcessSession session = new(
                worker, launch, terminal.StandardInput, terminal.StandardOutput, terminal.StandardError, inputAttached: true);
            Place(worker, session, isService: false, terminal.Arguments, terminal.WorkingDirectory, terminal.Environment);
            using CancellationTokenRegistration interrupt = cancellationToken.Register(session.Interrupt);
            return await session.Exit.ConfigureAwait(false);
        }
        finally
        {
            _pool.Release(group);
        }
    }

    /// <inheritdoc />
    public IRemoteService StartService(RemoteProcessLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        RemoteService service = new(this, launch);
        service.Start();
        return service;
    }

    /// <summary>Registers the session on its worker, hooks it up and sends the <c>start</c> message.</summary>
    private void Place(
        WorkerConnection worker,
        RemoteProcessSession session,
        bool isService,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        worker.Add(session);
        session.Attach();
        RemoteProcessLaunch launch = session.Launch!;
        IAppExecutionContext context = launch.Context;
        Type entryType = launch.Descriptor.EntryPointType;
        worker.Post(WorkerMessage.Create(WorkerMessageType.Start, writer =>
        {
            writer.WriteNumber("p", session.Pid);
            writer.WriteNumber("k", isService ? 1 : 0);
            writer.WriteString("asm", entryType.Assembly.GetName().Name + ".dll");
            writer.WriteString("ty", entryType.FullName);
            WriteStart(writer, session, context, arguments, workingDirectory, environment);
        }));
    }

    /// <inheritdoc />
    public bool CanRunBackgroundJobs => true;

    /// <inheritdoc />
    /// <remarks>
    /// ADR 0128 section 3: the task takes a core of the pool like a command (the ready worker when none
    /// is free), and gives it back when it ends. Its <c>start</c> message names the task instead of an
    /// entry point; the caller's cancellation is a Ctrl+C of the task's process. It waits in the pool
    /// queue with its process's niceness, like a command (ADR 0127 section 1).
    /// </remarks>
    public async Task<RemoteProcessExit> RunBackgroundJobAsync(RemoteBackgroundJobLaunch launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        WorkerConnection worker = await _pool.AcquireForCommandAsync(null, _pool.NiceOf(launch.Context.ProcessId), cancellationToken).ConfigureAwait(false);
        try
        {
            RemoteProcessSession session = new(worker, launch);
            worker.Add(session);
            session.Attach();
            IAppExecutionContext context = launch.Context;
            IAppEnvironmentGateway processEnvironment = context.Environment;
            HackerOs.AppSdk.Threading.HackBackgroundJobTarget target = launch.Request.Target;
            worker.Post(WorkerMessage.Create(WorkerMessageType.Start, writer =>
            {
                writer.WriteNumber("p", session.Pid);
                writer.WriteNumber("k", HackerOs.Platform.WorkerHost.Worker.WorkerProcessHost.JobKind);
                writer.WriteString("asm", target.AssemblyName + ".dll");
                writer.WriteString("ty", target.TypeName);
                writer.WriteStartObject("jt");
                writer.WriteString("asm", target.AssemblyName);
                writer.WriteString("ty", target.TypeName);
                if (target.MethodName is { } method)
                {
                    writer.WriteString("me", method);
                }

                writer.WriteString("it", target.InputTypeName);
                writer.WriteEndObject();
                writer.WriteString("ji", launch.Request.InputJson);
                WriteStart(
                    writer, session, context, processEnvironment.GetCommandLineArgs(), processEnvironment.CurrentDirectory,
                    processEnvironment.GetVariables());
            }));
            using CancellationTokenRegistration interrupt = cancellationToken.Register(session.Interrupt);
            return await session.Exit.ConfigureAwait(false);
        }
        finally
        {
            _pool.Release(null);
        }
    }

    /// <summary>
    /// Starts a window app in a worker of its own (trial of ADR 0129, verdict in ADR 0133): the
    /// worker renders <see cref="RemoteProcessLaunch.Descriptor"/>'s window component with
    /// <paramref name="runnerType"/> and sends its DOM changes back as <c>ui</c> messages, delivered to
    /// <paramref name="receiver"/> on the page's thread. The window's chrome stays on the page.
    /// </summary>
    /// <param name="launch">The window app's process (its context is the OS's own, as for a command).</param>
    /// <param name="runnerType">An <see cref="Worker.IWorkerWindowRunner"/> with a public parameterless constructor, loaded in the worker.</param>
    /// <param name="receiver">Receives each <c>ui</c> output; the element is valid during the call only.</param>
    /// <param name="cancellationToken">Cancels the wait for a worker.</param>
    /// <returns>The running window process.</returns>
    public async Task<RemoteWindowProcess> StartWindowAsync(
        RemoteProcessLaunch launch,
        Type runnerType,
        Action<System.Text.Json.JsonElement> receiver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(runnerType);
        ArgumentNullException.ThrowIfNull(receiver);
        WorkerConnection worker = await _pool.AcquireDedicatedAsync(cancellationToken).ConfigureAwait(false);
        RemoteProcessSession session = new(worker, launch, TextReader.Null, TextWriter.Null, TextWriter.Null, inputAttached: false)
        {
            UiReceiver = receiver
        };
        worker.Add(session);
        session.Attach();
        IAppExecutionContext context = launch.Context;
        IAppEnvironmentGateway environment = context.Environment;
        Type entryType = launch.Descriptor.EntryPointType;
        worker.Post(WorkerMessage.Create(WorkerMessageType.Start, writer =>
        {
            writer.WriteNumber("p", session.Pid);
            writer.WriteNumber("k", HackerOs.Platform.WorkerHost.Worker.WorkerProcessHost.WindowKind);
            writer.WriteString("asm", entryType.Assembly.GetName().Name + ".dll");
            writer.WriteString("ty", entryType.FullName);
            writer.WriteString("ra", runnerType.Assembly.GetName().Name + ".dll");
            writer.WriteString("rt", runnerType.FullName);
            WriteStart(writer, session, context, environment.GetCommandLineArgs(), environment.CurrentDirectory, environment.GetVariables());
        }));
        return new RemoteWindowProcess(_pool, worker, session);
    }

    /// <summary>The identity, environment and clock every <c>start</c> message carries.</summary>
    private void WriteStart(
        System.Text.Json.Utf8JsonWriter writer,
        RemoteProcessSession session,
        IAppExecutionContext context,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        IAppEnvironmentGateway processEnvironment = context.Environment;
        writer.WriteString("mf", AppManifestJsonSerializer.SerializeCanonical(context.Manifest));
        writer.WriteBoolean("in", session.InputAttached);
        WireCodec.WriteTime(writer, "cu", _pool.Clock.UtcNow);
        writer.WriteNumber("ck", _pool.Clock.CurrentTick);
        WireCodec.WriteStrings(writer, "argv", arguments);
        writer.WriteString("cwd", workingDirectory);
        WireCodec.WriteDictionary(writer, "envs", environment);

        writer.WriteStartObject("id");
        writer.WriteString("in", context.InstanceId.ToString("N"));
        writer.WriteString("u", context.UserId);
        writer.WriteString("home", context.HomeDirectory);
        writer.WriteBoolean("root", context.IsRootElevated);
        writer.WriteString("login", context.UserLoginName);
        writer.WriteString("disp", context.UserDisplayName);
        writer.WriteNumber("auth", (int)context.UserAuthority);
        WireCodec.WriteStrings(writer, "caps", context.GrantedCapabilities.Order(StringComparer.Ordinal));
        writer.WriteString("s", context.SessionId.Value.ToString("N"));
        WireCodec.WriteProcess(writer, "pr", context.Processes.OwnProcess);
        // ADR 0193: the session's time zone at start; the worker's own TZ still wins.
        writer.WriteString("tz", HackerOs.AppSdk.Globalization.HackTimeZone.GetId(context.TimeZone));
        if (context.Intent is { } intent)
        {
            WireCodec.WriteIntentDelivery(writer, "it", intent);
        }
        writer.WriteEndObject();

        writer.WriteStartObject("env");
        WireCodec.WriteStrings(writer, "argv", processEnvironment.GetCommandLineArgs());
        writer.WriteString("cwd", processEnvironment.CurrentDirectory);
        WireCodec.WriteDictionary(writer, "vars", processEnvironment.GetVariables());
        writer.WriteString("user", processEnvironment.UserName);
        writer.WriteString("host", processEnvironment.MachineName);
        writer.WriteEndObject();
    }

    /// <summary>A service running in a worker of its own (ADR 0070, section 3), or a web app in the web worker (ADR 0074).</summary>
    private sealed class RemoteService(WorkerRemoteProcessHost host, RemoteProcessLaunch launch) : IRemoteService
    {
        private readonly TaskCompletionSource<RemoteProcessSession> _session = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion { get; private set; } = Task.CompletedTask;

        public void Start() => Completion = RunAsync();

        public async Task StopAsync(ServiceStopReason reason, CancellationToken cancellationToken)
        {
            if (_session.Task.IsCompletedSuccessfully)
            {
                await _session.Task.Result.StopServiceAsync(reason, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task RunAsync()
        {
            IAppExecutionContext context = launch.Context;
            WorkerConnection worker;
            try
            {
                worker = AppExecutionHosts.Of(context.Manifest) == AppExecutionHost.Web
                    ? await host._pool.AcquireWebAsync(context.CancellationToken).ConfigureAwait(false)
                    : await host._pool.AcquireDedicatedAsync(context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _session.TrySetException(exception);
                throw;
            }

            // A service started by a shell writes into its terminal (ADR 0064); otherwise /dev/null.
            IAppConsoleGateway console = context.Console;
            RemoteProcessSession session = new(worker, launch, console.In, console.Out, console.Error, inputAttached: console.IsAttached);
            _session.TrySetResult(session);
            IAppEnvironmentGateway environment = context.Environment;
            host.Place(worker, session, isService: true, environment.GetCommandLineArgs(), environment.CurrentDirectory, environment.GetVariables());
            RemoteProcessExit exit = await session.Exit.ConfigureAwait(false);
            switch (exit.Status)
            {
                case RemoteProcessExitStatus.Exited:
                    return;
                case RemoteProcessExitStatus.Cancelled:
                    throw new OperationCanceledException("The service honored its stop request.");
                case RemoteProcessExitStatus.Killed:
                    throw new HackProcessKilledException(context.ProcessId.Value);
                default:
                    throw new RemoteProcessFaultException(exit.FaultMessage ?? "The service faulted in its worker.");
            }
        }
    }
}
