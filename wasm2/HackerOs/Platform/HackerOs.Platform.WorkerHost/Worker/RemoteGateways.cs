using HackerOs.AppSdk.Diagnostics;
using System.Net;
using System.Text.Json;
using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Apps;
using HackerOs.AppSdk.Clipboard;
using HackerOs.AppSdk.IO;
using HackerOs.AppSdk.Net;
using HackerOs.Platform.Core.Processes;
using HackerOs.Platform.WorkerHost.Protocol;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Processes;

namespace HackerOs.Platform.WorkerHost.Worker;

/// <summary>
/// One process's line to the OS from inside a worker: its calls carry its PID, flush its pending
/// output first (so its writes and its calls stay in order), refuse to run once it was killed
/// (ADR 0069, section 3), and count as kernel time in the worker's CPU meter (ADR 0067, section 2).
/// </summary>
internal sealed class RemoteProcessChannel
{
    private readonly WorkerRpcClient _rpc;
    private readonly RemoteProcessOutput _output;
    private readonly ProcessControlBlock _control;
    private readonly Action<string>? _post;

    public RemoteProcessChannel(long pid, WorkerRpcClient rpc, RemoteProcessOutput output, ProcessControlBlock control, Action<string>? post = null)
    {
        Pid = pid;
        _rpc = rpc;
        _output = output;
        _control = control;
        _post = post;
    }

    /// <summary>Gets the listening sockets the process opened (ADR 0074, section 2).</summary>
    public RemoteListenerTable Listeners { get; } = new();

    /// <summary>Posts a raw protocol message (the reply to an HTTP request), outside the call/answer pairs.</summary>
    public void Post(string message) =>
        (_post ?? throw new InvalidOperationException("This channel cannot post messages."))(message);

    /// <summary>Gets the calling process.</summary>
    public long Pid { get; }

    /// <summary>Calls the OS and waits for its answer.</summary>
    public async Task<JsonElement> CallAsync(string operation, Action<Utf8JsonWriter>? arguments, CancellationToken cancellationToken)
    {
        _control.ThrowIfKilled();
        _output.Flush();
        using (HackExecutionContext.EnterKernel())
        {
            return await _rpc.CallAsync(Pid, operation, arguments, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Gets whether this process may wait for the OS's answer (ADR 0131, section 4).</summary>
    public bool CanCallSynchronously => _rpc.CanCallSynchronously;

    /// <summary>
    /// Calls the OS and blocks the worker until it answers (ADR 0131, section 4); only on a cross-origin
    /// isolated page (<see cref="CanCallSynchronously"/>). Output is flushed first, as before any call.
    /// </summary>
    public JsonElement CallSync(string operation, Action<Utf8JsonWriter>? arguments)
    {
        _control.ThrowIfKilled();
        _output.Flush();
        using (HackExecutionContext.EnterKernel())
        {
            return _rpc.CallSync(Pid, operation, arguments);
        }
    }

    /// <summary>Posts a call that has no answer, in order with the others.</summary>
    public void Notify(string operation, Action<Utf8JsonWriter>? arguments)
    {
        _control.ThrowIfKilled();
        _output.Flush();
        _rpc.Notify(Pid, operation, arguments);
    }
}

/// <summary>
/// <see cref="IAppEnvironmentGateway"/> in a worker: the process's environment is copied at start, read
/// locally like <c>getenv</c>, and every change is written through to the OS's process table, so
/// <c>/proc/&lt;pid&gt;/environ</c> and <c>cwd</c> follow it (ADR 0050, section 6).
/// </summary>
internal sealed class RemoteEnvironmentGateway(
    RemoteProcessChannel channel,
    IReadOnlyList<string> arguments,
    string currentDirectory,
    Dictionary<string, string> variables,
    string userName,
    string machineName) : IAppEnvironmentGateway
{
    private readonly Lock _gate = new();
    private string _currentDirectory = currentDirectory;

    public string CurrentDirectory
    {
        get
        {
            lock (_gate)
            {
                return _currentDirectory;
            }
        }
    }

    public string UserName => userName;

    public string MachineName => machineName;

    public IReadOnlyList<string> GetCommandLineArgs() => arguments;

    public void SetCurrentDirectory(VirtualPath directory)
    {
        lock (_gate)
        {
            _currentDirectory = directory.Value;
            variables["PWD"] = directory.Value;
        }

        channel.Notify(RemoteOps.EnvSetDirectory, writer => writer.WriteString("p", directory.Value));
    }

    public string? GetVariable(string name)
    {
        lock (_gate)
        {
            return variables.GetValueOrDefault(name);
        }
    }

    public IReadOnlyDictionary<string, string> GetVariables()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(variables, StringComparer.Ordinal);
        }
    }

    public void SetVariable(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Contains('=') || name.Contains('\0') || value?.Contains('\0') == true)
        {
            throw new ArgumentException($"'{name}' is not a valid environment variable assignment.", nameof(name));
        }

        lock (_gate)
        {
            if (value is null)
            {
                variables.Remove(name);
            }
            else
            {
                variables[name] = value;
            }
        }

        channel.Notify(RemoteOps.EnvSetVariable, writer =>
        {
            writer.WriteString("n", name);
            if (value is not null)
            {
                writer.WriteString("v", value);
            }
        });
    }
}

/// <summary><see cref="IAppConsoleGateway"/> in a worker: the process's message streams.</summary>
internal sealed class RemoteConsoleGateway(bool attached, TextReader input, TextWriter output, TextWriter error) : IAppConsoleGateway
{
    public bool IsAttached => attached;

    public TextReader In => input;

    public TextWriter Out => output;

    public TextWriter Error => error;
}

/// <summary><see cref="IAppLoggingGateway"/> in a worker: each entry is posted to <c>/var/log/apps/&lt;appId&gt;.log</c>.</summary>
internal sealed class RemoteLoggingGateway(RemoteProcessChannel channel) : IAppLoggingGateway
{
    public void Log(DiagnosticSeverity severity, string message, IReadOnlyDictionary<string, string>? properties = null) =>
        channel.Notify(RemoteOps.Log, writer =>
        {
            writer.WriteNumber("s", (int)severity);
            writer.WriteString("m", message);
            if (properties is not null)
            {
                WireCodec.WriteDictionary(writer, "pr", properties);
            }
        });

    /// <summary>
    /// Posts a daemon log line (ADR 0080, section 3). The path is checked here so a bad call fails in
    /// the app; the system-app rule is checked by the OS, which drops the line of any other app.
    /// </summary>
    public void AppendToLogFile(string relativePath, string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Split('/').Length != 2 || !relativePath.EndsWith(".log", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{relativePath}' is not a daemon log path ('<directory>/<file>.log').", nameof(relativePath));
        }

        channel.Notify(RemoteOps.LogFile, writer =>
        {
            writer.WriteString("p", relativePath);
            writer.WriteString("m", line);
        });
    }

    /// <summary>Posts a line of the site's debug log (ADR 0088, section 5); the OS drops it for an app that is not a web app.</summary>
    public void AppendToSiteDebugLog(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        channel.Notify(RemoteOps.LogSite, writer => writer.WriteString("m", line));
    }
}

/// <summary>
/// <see cref="IAppClockGateway"/> in a worker: the simulated time as of the last synchronization (the
/// start of the process, then each heartbeat answer, every 250 ms); waits are timed by the OS's clock.
/// </summary>
internal sealed class RemoteClockGateway(RemoteProcessChannel channel, WorkerClock clock) : IAppClockGateway
{
    public DateTimeOffset UtcNow => clock.UtcNow;

    public long CurrentTick => clock.CurrentTick;

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        CancellationTokenSource cancel = new();
        _ = RunAsync();
        return cancel;

        async Task RunAsync()
        {
            try
            {
                await DelayAsync(delay, cancel.Token).ConfigureAwait(false);
                callback();
            }
            catch (OperationCanceledException)
            {
                // Disposed before it fired.
            }
        }
    }

    public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        await channel.CallAsync(RemoteOps.ClockDelay, writer => writer.WriteNumber("d", delay.Ticks), cancellationToken).ConfigureAwait(false);
}

/// <summary>The simulated clock as the worker last saw it.</summary>
internal sealed class WorkerClock
{
    private readonly Lock _gate = new();
    private DateTimeOffset _utcNow;
    private long _tick = long.MinValue;

    public WorkerClock(DateTimeOffset utcNow, long tick) => Update(utcNow, tick);

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }
    }

    public long CurrentTick
    {
        get
        {
            lock (_gate)
            {
                return _tick;
            }
        }
    }

    /// <summary>Takes the OS's clock: the start of a process, then every heartbeat answer.</summary>
    public void Update(DateTimeOffset utcNow, long tick)
    {
        lock (_gate)
        {
            if (tick < _tick || (tick == _tick && utcNow < _utcNow))
            {
                return; // An older answer arrived late: time never goes backwards.
            }

            _utcNow = utcNow;
            _tick = tick;
        }
    }
}

/// <summary>
/// <see cref="IAppProcessGateway"/> in a worker. Listing and stopping are calls to the OS. Without
/// cross-origin isolation the synchronous members cannot wait for it from a worker's single thread:
/// <see cref="Kill"/> of the process itself sends the <c>SIGKILL</c> and returns the expected record,
/// <see cref="ListProcesses"/> and <see cref="Kill"/> of another process are refused (use
/// <c>HackProcess.GetProcessesAsync</c> and <c>HackProcess.KillAsync</c>). On an isolated page
/// (ADR 0131, section 4) both wait for the OS with <c>Atomics.wait</c>, like local calls.
/// <see cref="StartChild"/> is not offered.
/// </summary>
internal sealed class RemoteProcessGateway(RemoteProcessChannel channel, ProcessRecord ownProcess) : IAppProcessGateway
{
    public ProcessRecord OwnProcess => ownProcess;

    public ProcessRecord StartChild(string appId, AppInstanceId appInstanceId, AppKind kind, ResourceProfile resourceProfile) =>
        throw RemoteUnsupported.Member("IAppProcessGateway.StartChild");

    public IReadOnlyList<ProcessRecord> ListProcesses() =>
        channel.CanCallSynchronously
            ? WireCodec.ReadProcesses(channel.CallSync(RemoteOps.ProcessList, null), "ps")
            : throw RemoteUnsupported.Member("IAppProcessGateway.ListProcesses (synchronous; read /proc or use HackProcess.GetProcessesAsync)");

    public async Task<ProcessRecord> StopAsync(
        ProcessId pid, TimeSpan timeout, ProcessExitReason reason = ProcessExitReason.CloseRequested, CancellationToken cancellationToken = default)
    {
        JsonElement value = await channel.CallAsync(RemoteOps.ProcessStop, writer =>
        {
            writer.WriteNumber("pid", pid.Value);
            writer.WriteNumber("to", timeout.Ticks);
            writer.WriteNumber("r", (int)reason);
        }, cancellationToken).ConfigureAwait(false);
        return WireCodec.ReadProcess(value.GetProperty("pr"));
    }

    public ProcessRecord Kill(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Killed)
    {
        if (ownProcess.Pid != pid && channel.CanCallSynchronously)
        {
            JsonElement value = channel.CallSync(RemoteOps.ProcessKill, writer =>
            {
                writer.WriteNumber("pid", pid.Value);
                writer.WriteNumber("r", (int)reason);
            });
            return WireCodec.ReadProcess(value.GetProperty("pr"));
        }

        channel.Notify(RemoteOps.ProcessKill, writer =>
        {
            writer.WriteNumber("pid", pid.Value);
            writer.WriteNumber("r", (int)reason);
        });
        return ownProcess.Pid == pid ? ownProcess : throw RemoteUnsupported.Member("IAppProcessGateway.Kill of another process (synchronous; use HackProcess.KillAsync)");
    }

    public async Task<ProcessRecord> KillAsync(ProcessId pid, ProcessExitReason reason, CancellationToken cancellationToken)
    {
        JsonElement value = await channel.CallAsync(RemoteOps.ProcessKill, writer =>
        {
            writer.WriteNumber("pid", pid.Value);
            writer.WriteNumber("r", (int)reason);
        }, cancellationToken).ConfigureAwait(false);
        return WireCodec.ReadProcess(value.GetProperty("pr"));
    }

    public async Task<ResourceLimitChangeResult> SetResourceLimitAsync(
        ProcessId pid, ResourceLimitKind kind, ResourceLimit limit, CancellationToken cancellationToken = default)
    {
        JsonElement value = await channel.CallAsync(RemoteOps.ProcessSetLimit, writer =>
        {
            writer.WriteNumber("pid", pid.Value);
            writer.WriteNumber("k", (int)kind);
            writer.WriteNumber("s", limit.Soft);
            writer.WriteNumber("h", limit.Hard);
        }, cancellationToken).ConfigureAwait(false);
        return (ResourceLimitChangeResult)value.GetInt32("r");
    }
}

/// <summary><see cref="IAppNetworkGateway"/> in a worker: the OS's network stack, with its real-network fallback.</summary>
internal sealed class RemoteNetworkGateway(RemoteProcessChannel channel) : IAppNetworkGateway
{
    public async ValueTask<HackNetworkResult<HackHostEntry>> ResolveAsync(string hostName, CancellationToken cancellationToken = default) =>
        WireCodec.ReadNetwork(await channel.CallAsync(RemoteOps.NetResolve, writer => writer.WriteString("h", hostName), cancellationToken).ConfigureAwait(false),
            WireCodec.ReadHostEntry);

    public async ValueTask<HackNetworkResult<HackHostEntry>> ReverseLookupAsync(IPAddress address, CancellationToken cancellationToken = default) =>
        WireCodec.ReadNetwork(await channel.CallAsync(RemoteOps.NetReverse, writer => writer.WriteString("ad", address.ToString()), cancellationToken).ConfigureAwait(false),
            WireCodec.ReadHostEntry);

    public async ValueTask<HackNetworkResult<HackPingReply>> PingAsync(string hostNameOrAddress, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        WireCodec.ReadNetwork(await channel.CallAsync(RemoteOps.NetPing, writer =>
        {
            writer.WriteString("h", hostNameOrAddress);
            writer.WriteNumber("to", timeout.Ticks);
        }, cancellationToken).ConfigureAwait(false), WireCodec.ReadPingReply);

    public async ValueTask<HackNetworkResult<HackHttpResponse>> SendAsync(HackHttpRequest request, CancellationToken cancellationToken = default) =>
        WireCodec.ReadNetwork(await channel.CallAsync(RemoteOps.NetHttp, writer => WireCodec.WriteHttpRequest(writer, request), cancellationToken).ConfigureAwait(false),
            WireCodec.ReadHttpResponse);

    public async ValueTask<HackNetworkResult<IReadOnlyList<HackPortProbeResult>>> ProbeTcpAsync(
        string hostNameOrAddress, int firstPort, int lastPort, CancellationToken cancellationToken = default) =>
        WireCodec.ReadNetwork(await channel.CallAsync(RemoteOps.NetTcp, writer =>
        {
            writer.WriteString("h", hostNameOrAddress);
            writer.WriteNumber("f", firstPort);
            writer.WriteNumber("l", lastPort);
        }, cancellationToken).ConfigureAwait(false), WireCodec.ReadPortProbes);

    public async ValueTask<HackNetworkResult<HackOsFingerprint?>> DetectOperatingSystemAsync(
        string hostNameOrAddress, CancellationToken cancellationToken = default) =>
        WireCodec.ReadNetwork(await channel.CallAsync(RemoteOps.NetOs, writer => writer.WriteString("h", hostNameOrAddress), cancellationToken).ConfigureAwait(false),
            WireCodec.ReadFingerprint);

    /// <summary>
    /// Opens the socket in the OS (its table of listening ports), then receives its requests as
    /// <c>hreq</c> messages (ADR 0074, section 2): the site runs in the worker, the port lives in the OS.
    /// </summary>
    public async ValueTask<HackNetworkResult<IHackHttpListenerSocket>> ListenHttpAsync(
        IReadOnlyList<string> prefixes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        JsonElement answer = await channel.CallAsync(
            RemoteOps.NetListen, writer => WireCodec.WriteStrings(writer, "px", prefixes), cancellationToken).ConfigureAwait(false);
        HackNetworkError error = (HackNetworkError)answer.GetInt32("ne");
        if (error != HackNetworkError.None)
        {
            return HackNetworkResult<IHackHttpListenerSocket>.Failure(error, answer.GetOptionalString("nm"));
        }

        RemoteHttpListenerSocket socket = new(channel, answer.GetInt32("sk"), WireCodec.ReadStrings(answer, "px"));
        channel.Listeners.Add(socket);
        return HackNetworkResult<IHackHttpListenerSocket>.Success(socket);
    }

    public async ValueTask<IReadOnlyList<HackTcpListenerInfo>> GetTcpListenersAsync(CancellationToken cancellationToken = default) =>
        WireCodec.ReadListeners(await channel.CallAsync(RemoteOps.NetListeners, null, cancellationToken).ConfigureAwait(false));
}

/// <summary>
/// The listening sockets of one process in a worker, by the id the OS gave them. A request that
/// arrives before the answer naming its socket waits here; one for a closed socket is dropped (the
/// OS has already reset its connection).
/// </summary>
internal sealed class RemoteListenerTable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, RemoteHttpListenerSocket> _sockets = [];
    private readonly Dictionary<int, List<HackHttpListenerExchange>> _early = [];
    private readonly HashSet<int> _closed = [];
    private bool _allClosed;

    /// <summary>Registers a socket the OS just opened and hands it the requests that came first.</summary>
    public void Add(RemoteHttpListenerSocket socket)
    {
        List<HackHttpListenerExchange>? early;
        lock (_gate)
        {
            _sockets[socket.Id] = socket;
            _early.Remove(socket.Id, out early);
        }

        foreach (HackHttpListenerExchange exchange in early ?? [])
        {
            socket.Enqueue(exchange);
        }
    }

    /// <summary>Hands one request to its socket.</summary>
    public void Deliver(int id, HackHttpListenerExchange exchange)
    {
        RemoteHttpListenerSocket? socket;
        lock (_gate)
        {
            if (_allClosed || _closed.Contains(id))
            {
                return;
            }

            if (!_sockets.TryGetValue(id, out socket))
            {
                if (!_early.TryGetValue(id, out List<HackHttpListenerExchange>? waiting))
                {
                    waiting = [];
                    _early[id] = waiting;
                }

                waiting.Add(exchange);
                return;
            }
        }

        socket.Enqueue(exchange);
    }

    /// <summary>Forgets a socket its process closed.</summary>
    public void Remove(int id)
    {
        lock (_gate)
        {
            _sockets.Remove(id);
            _early.Remove(id);
            _closed.Add(id);
        }
    }

    /// <summary>The process ended: every socket stops accepting.</summary>
    public void CloseAll()
    {
        List<RemoteHttpListenerSocket> sockets;
        lock (_gate)
        {
            _allClosed = true;
            sockets = [.. _sockets.Values];
            _sockets.Clear();
            _early.Clear();
        }

        foreach (RemoteHttpListenerSocket socket in sockets)
        {
            socket.Complete();
        }
    }
}

/// <summary>A listening socket seen from a worker: requests arrive as messages, replies leave as <c>hres</c>.</summary>
internal sealed class RemoteHttpListenerSocket(RemoteProcessChannel channel, int id, IReadOnlyList<string> prefixes) : IHackHttpListenerSocket
{
    private readonly System.Threading.Channels.Channel<HackHttpListenerExchange> _queue =
        System.Threading.Channels.Channel.CreateUnbounded<HackHttpListenerExchange>();
    private int _disposed;

    /// <summary>Gets the id the OS gave the socket.</summary>
    public int Id { get; } = id;

    public IReadOnlyList<string> Prefixes { get; } = prefixes;

    public async ValueTask<HackHttpListenerExchange?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_queue.Reader.TryRead(out HackHttpListenerExchange? exchange))
            {
                return exchange;
            }
        }

        return null;
    }

    public void Respond(long exchangeId, HackHttpListenerReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        channel.Post(WorkerMessage.Create(WorkerMessageType.HttpResponse, writer =>
        {
            writer.WriteNumber("p", channel.Pid);
            writer.WriteNumber("sk", Id);
            writer.WriteNumber("x", exchangeId);
            WireCodec.WriteListenerReply(writer, "r", reply);
        }));
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            channel.Listeners.Remove(Id);
            Complete();
            try
            {
                channel.Notify(RemoteOps.NetUnlisten, writer => writer.WriteNumber("sk", Id));
            }
            catch (Exception exception) when (exception is HackerOs.AppSdk.Diagnostics.HackProcessKilledException or InvalidOperationException)
            {
                // The process is going away: the OS closes its sockets itself.
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Queues a request for <see cref="AcceptAsync"/>.</summary>
    public void Enqueue(HackHttpListenerExchange exchange) => _queue.Writer.TryWrite(exchange);

    /// <summary>Ends the queue: <see cref="AcceptAsync"/> returns <see langword="null"/>.</summary>
    public void Complete() => _queue.Writer.TryComplete();
}

/// <summary><see cref="IAppCatalogGateway"/> in a worker. <see cref="Changed"/> is never raised: a process in a worker reads the catalog again when it needs it.</summary>
internal sealed class RemoteCatalogGateway(RemoteProcessChannel channel) : IAppCatalogGateway
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public async ValueTask<IReadOnlyList<HackAppInfo>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
        WireCodec.ReadApps(await channel.CallAsync(RemoteOps.CatalogInstalled, null, cancellationToken).ConfigureAwait(false));

    public async ValueTask<IReadOnlyList<HackAppInfo>> GetHandlersAsync(
        VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default) =>
        WireCodec.ReadApps(await channel.CallAsync(RemoteOps.CatalogHandlers, writer =>
        {
            writer.WriteString("p", path.Value);
            if (mediaType is not null)
            {
                writer.WriteString("mt", mediaType);
            }
        }, cancellationToken).ConfigureAwait(false));

    public async ValueTask<IReadOnlyList<HackFileAssociation>> GetDefaultHandlersAsync(CancellationToken cancellationToken = default) =>
        WireCodec.ReadAssociations(await channel.CallAsync(RemoteOps.CatalogDefaults, null, cancellationToken).ConfigureAwait(false));

    public async ValueTask<HackDefaultHandlerStatus> SetUserDefaultHandlerAsync(
        string target, string action, string? appId, CancellationToken cancellationToken = default) =>
        (HackDefaultHandlerStatus)(await channel.CallAsync(RemoteOps.CatalogSetDefault, writer =>
        {
            writer.WriteString("t", target);
            writer.WriteString("a", action);
            if (appId is not null)
            {
                writer.WriteString("id", appId);
            }
        }, cancellationToken).ConfigureAwait(false)).GetInt32("s");
}

/// <summary><see cref="IAppServiceControlGateway"/> in a worker (<c>systemctl</c>).</summary>
internal sealed class RemoteServiceControlGateway(RemoteProcessChannel channel) : IAppServiceControlGateway
{
    public async ValueTask<ServiceControlResult> StartAsync(string serviceAppId, CancellationToken cancellationToken = default) =>
        WireCodec.ReadServiceResult(await channel.CallAsync(RemoteOps.ServiceStart, writer => writer.WriteString("id", serviceAppId), cancellationToken).ConfigureAwait(false));

    public async ValueTask<ServiceControlResult> StopAsync(string serviceAppId, CancellationToken cancellationToken = default) =>
        WireCodec.ReadServiceResult(await channel.CallAsync(RemoteOps.ServiceStop, writer => writer.WriteString("id", serviceAppId), cancellationToken).ConfigureAwait(false));

    public async ValueTask<ServiceStartMode> GetStartModeAsync(string serviceAppId, CancellationToken cancellationToken = default) =>
        (ServiceStartMode)(await channel.CallAsync(RemoteOps.ServiceGetMode, writer => writer.WriteString("id", serviceAppId), cancellationToken).ConfigureAwait(false))
            .GetInt32("m");

    public async ValueTask<ServiceControlResult> SetStartModeAsync(
        string serviceAppId, ServiceStartMode mode, CancellationToken cancellationToken = default) =>
        WireCodec.ReadServiceResult(await channel.CallAsync(RemoteOps.ServiceSetMode, writer =>
        {
            writer.WriteString("id", serviceAppId);
            writer.WriteNumber("m", (int)mode);
        }, cancellationToken).ConfigureAwait(false));

    public async ValueTask<ServiceStatusSnapshot> GetStatusAsync(string serviceAppId, CancellationToken cancellationToken = default) =>
        WireCodec.ReadServiceStatus(await channel.CallAsync(RemoteOps.ServiceStatus, writer => writer.WriteString("id", serviceAppId), cancellationToken).ConfigureAwait(false));

    public async ValueTask<ServiceControlResult> ResetFailedAsync(string serviceAppId, CancellationToken cancellationToken = default) =>
        WireCodec.ReadServiceResult(await channel.CallAsync(RemoteOps.ServiceResetFailed, writer => writer.WriteString("id", serviceAppId), cancellationToken).ConfigureAwait(false));
}

/// <summary>
/// <see cref="IAppServiceNotifyGateway"/> of a service in a worker (ADR 0097, section 2): each
/// message is posted to the OS, which applies it to the service's state under this process's PID.
/// </summary>
internal sealed class RemoteServiceNotifyGateway(RemoteProcessChannel channel) : IAppServiceNotifyGateway
{
    public void Notify(ServiceNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        channel.Notify(RemoteOps.ServiceNotify, writer => WireCodec.WriteServiceNotification(writer, notification));
    }
}

/// <summary><see cref="IAppDriveGateway"/> in a worker (<c>df</c>).</summary>
internal sealed class RemoteDriveGateway(RemoteProcessChannel channel) : IAppDriveGateway
{
    public async ValueTask<IReadOnlyList<HackDriveSnapshot>> GetDrivesAsync(CancellationToken cancellationToken = default) =>
        WireCodec.ReadDrives(await channel.CallAsync(RemoteOps.Drives, null, cancellationToken).ConfigureAwait(false));

    public async ValueTask<bool> IsHostFolderSupportedAsync(CancellationToken cancellationToken = default) =>
        (await channel.CallAsync(RemoteOps.DriveHostSupported, null, cancellationToken).ConfigureAwait(false)).GetProperty("s").GetBoolean();

    // ADR 0181: the browser's folder dialog opens on the page; the user gesture that led here is the page's.
    public async ValueTask<HackDriveSnapshot?> MountHostFolderAsync(string? mountPoint, CancellationToken cancellationToken = default) =>
        WireCodec.ReadDrive(await channel.CallAsync(RemoteOps.DriveMount, writer =>
        {
            if (mountPoint is not null)
            {
                writer.WriteString("p", mountPoint);
            }
        }, cancellationToken).ConfigureAwait(false));

    public async ValueTask<bool> ReconnectAsync(string mountPoint, CancellationToken cancellationToken = default) =>
        (await channel.CallAsync(RemoteOps.DriveReconnect, writer => writer.WriteString("p", mountPoint), cancellationToken)
            .ConfigureAwait(false)).GetProperty("s").GetBoolean();

    public async ValueTask UnmountAsync(string mountPoint, CancellationToken cancellationToken = default) =>
        await channel.CallAsync(RemoteOps.DriveUnmount, writer => writer.WriteString("p", mountPoint), cancellationToken).ConfigureAwait(false);
}

/// <summary><see cref="IAppCrossOriginIsolationGateway"/> in a worker (ADR 0146): the OS answers with the page's status.</summary>
internal sealed class RemoteCrossOriginIsolationGateway(RemoteProcessChannel channel) : IAppCrossOriginIsolationGateway
{
    public async ValueTask<HackCrossOriginIsolationStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        WireCodec.ReadCrossOriginIsolation(await channel.CallAsync(RemoteOps.CrossOriginIsolation, null, cancellationToken).ConfigureAwait(false));
}

/// <summary><see cref="IAppIntentGateway"/> in a worker (<c>launch</c>): the OS opens the app on the desktop.</summary>
internal sealed class RemoteIntentGateway(RemoteProcessChannel channel) : IAppIntentGateway
{
    public async ValueTask<AppIntentLaunchResult> LaunchAsync(string appId, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
        WireCodec.ReadLaunchResult(await channel.CallAsync(RemoteOps.IntentLaunch, writer =>
        {
            writer.WriteString("id", appId);
            WireCodec.WriteStrings(writer, "ar", arguments);
        }, cancellationToken).ConfigureAwait(false));

    public async ValueTask<AppIntentOpenFileResult> OpenFileAsync(VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default) =>
        WireCodec.ReadOpenFileResult(await channel.CallAsync(RemoteOps.IntentOpenFile, writer =>
        {
            writer.WriteString("p", path.Value);
            if (mediaType is not null)
            {
                writer.WriteString("mt", mediaType);
            }
        }, cancellationToken).ConfigureAwait(false));

    public async ValueTask<AppIntentOpenFileResult> EditFileAsync(VirtualPath path, string? mediaType = null, CancellationToken cancellationToken = default) =>
        WireCodec.ReadOpenFileResult(await channel.CallAsync(RemoteOps.IntentEditFile, writer =>
        {
            writer.WriteString("p", path.Value);
            if (mediaType is not null)
            {
                writer.WriteString("mt", mediaType);
            }
        }, cancellationToken).ConfigureAwait(false));

    public async ValueTask<AppIntentResult> RevealFileAsync(VirtualPath path, CancellationToken cancellationToken = default) =>
        WireCodec.ReadIntentResult(await channel.CallAsync(
            RemoteOps.IntentReveal, writer => writer.WriteString("p", path.Value), cancellationToken).ConfigureAwait(false));

    public async ValueTask<AppIntentResult> OpenUriAsync(Uri uri, string? preferredAppId = null, CancellationToken cancellationToken = default) =>
        WireCodec.ReadIntentResult(await channel.CallAsync(RemoteOps.IntentOpenUri, writer =>
        {
            writer.WriteString("u", uri.OriginalString);
            if (preferredAppId is not null)
            {
                writer.WriteString("app", preferredAppId);
            }
        }, cancellationToken).ConfigureAwait(false));

    public async ValueTask<AppIntentResult> ShowAppSettingsAsync(string appId, CancellationToken cancellationToken = default) =>
        WireCodec.ReadIntentResult(await channel.CallAsync(
            RemoteOps.IntentShowSettings, writer => writer.WriteString("id", appId), cancellationToken).ConfigureAwait(false));

    public async ValueTask<AppIntentResult> SendIntentAsync(
        string intentId, string? payloadJson, string? targetAppId = null, bool rememberChoice = false, CancellationToken cancellationToken = default) =>
        WireCodec.ReadIntentResult(await channel.CallAsync(RemoteOps.IntentSend, writer =>
        {
            writer.WriteString("id", intentId);
            if (payloadJson is not null)
            {
                writer.WriteString("pl", payloadJson);
            }

            if (targetAppId is not null)
            {
                writer.WriteString("app", targetAppId);
            }

            if (rememberChoice)
            {
                writer.WriteBoolean("rm", true);
            }
        }, cancellationToken).ConfigureAwait(false));
}

/// <summary>
/// <see cref="HackerOs.AppSdk.WebData.IAppWebDatabaseGateway"/> in a worker (ADR 0088, section 1): the schema
/// and each batch travel as JSON to the OS, which opens <c>hackweb.&lt;app id&gt;</c> on the main thread.
/// </summary>
internal sealed class RemoteWebDatabaseGateway(RemoteProcessChannel channel, string appId) : HackerOs.AppSdk.WebData.IAppWebDatabaseGateway
{
    public string DatabaseName { get; } = HackerOs.AppSdk.WebData.HackWebDbNames.ForApp(appId);

    public async ValueTask OpenAsync(HackerOs.AppSdk.WebData.HackWebDbSchemaDefinition schema, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        string json = JsonSerializer.Serialize(schema, HackerOs.AppSdk.WebData.HackWebDbJsonContext.Default.HackWebDbSchemaDefinition);
        await channel.CallAsync(RemoteOps.WebDbOpen, writer => writer.WriteString("j", json), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<string?>> ExecuteAsync(HackerOs.AppSdk.WebData.HackWebDbRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string json = JsonSerializer.Serialize(request, HackerOs.AppSdk.WebData.HackWebDbJsonContext.Default.HackWebDbRequest);
        JsonElement result = await channel.CallAsync(RemoteOps.WebDbExecute, writer => writer.WriteString("j", json), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(result.GetString("r"), HackerOs.AppSdk.WebData.HackWebDbJsonContext.Default.ListString) ?? [];
    }
}

/// <summary><see cref="IAppClipboardGateway"/> in a worker. <see cref="Changed"/> is never raised.</summary>
internal sealed class RemoteClipboardGateway(RemoteProcessChannel channel) : IAppClipboardGateway
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public async ValueTask<string?> GetTextAsync(CancellationToken cancellationToken = default) =>
        (await channel.CallAsync(RemoteOps.ClipboardGetText, null, cancellationToken).ConfigureAwait(false)).GetOptionalString("tx");

    public async ValueTask SetTextAsync(string text, CancellationToken cancellationToken = default) =>
        await channel.CallAsync(RemoteOps.ClipboardSetText, writer => writer.WriteString("tx", text), cancellationToken).ConfigureAwait(false);

    public async ValueTask<HackClipboardFileList?> GetFileDropListAsync(CancellationToken cancellationToken = default) =>
        WireCodec.ReadClipboardFiles(await channel.CallAsync(RemoteOps.ClipboardGetFiles, null, cancellationToken).ConfigureAwait(false));

    public async ValueTask SetFileDropListAsync(HackClipboardFileList files, CancellationToken cancellationToken = default) =>
        await channel.CallAsync(RemoteOps.ClipboardSetFiles, writer => WireCodec.WriteClipboardFiles(writer, files), cancellationToken).ConfigureAwait(false);

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default) =>
        await channel.CallAsync(RemoteOps.ClipboardClear, null, cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// <see cref="IAppNotificationGateway"/> in a worker: the notification is posted to the OS without
/// waiting; the returned identifier is the worker's own (the desktop keeps its own one).
/// </summary>
internal sealed class RemoteNotificationGateway(RemoteProcessChannel channel) : IAppNotificationGateway
{
    public NotificationId Post(
        NotificationSeverity severity,
        string title,
        string message,
        IReadOnlyList<NotificationAction>? actions = null,
        TimeSpan? expiresAfter = null)
    {
        channel.Notify(RemoteOps.NotificationPost, writer =>
        {
            writer.WriteNumber("s", (int)severity);
            writer.WriteString("ti", title);
            writer.WriteString("m", message);
            if (actions is not null)
            {
                WireCodec.WritePairs(writer, "ac", actions.Select(static action => new KeyValuePair<string, string>(action.Id, action.Label)));
            }

            if (expiresAfter is { } expiry)
            {
                writer.WriteNumber("ex", expiry.Ticks);
            }
        });
        return NotificationId.FromGuid(Guid.NewGuid());
    }
}

/// <summary>
/// <see cref="ICapabilityChecker"/> in a worker, from the capabilities granted when the process
/// started. The OS checks every remote call again with its live policy: this only answers the app's
/// own questions.
/// </summary>
internal sealed class RemoteCapabilityChecker(string appId, string userId, IReadOnlySet<string> granted) : ICapabilityChecker
{
    public CapabilityPolicyEvaluation Evaluate(
        string capability,
        AppAuthority requiredAuthority = AppAuthority.User,
        CapabilityResourceCandidate? resourceCandidate = null) =>
        granted.Contains(capability)
            ? CapabilityPolicyEvaluation.Permit(new CapabilityGrant(
                CapabilityGrantId.FromGuid(Guid.NewGuid()), appId, userId, capability, 1, CapabilityGrantSource.SystemPolicy))
            : CapabilityPolicyEvaluation.DenyMissing(1);

    public void Require(
        string capability,
        AppAuthority requiredAuthority = AppAuthority.User,
        CapabilityResourceCandidate? resourceCandidate = null)
    {
        CapabilityPolicyEvaluation evaluation = Evaluate(capability, requiredAuthority, resourceCandidate);
        if (!evaluation.Granted)
        {
            throw new AppGatewayAccessDeniedException(capability, evaluation);
        }
    }
}

/// <summary><see cref="IAppPermissionErrorGateway"/> in a worker: raised when the OS reports a denial on this process's calls.</summary>
internal sealed class RemotePermissionErrorGateway : IAppPermissionErrorGateway
{
    public event EventHandler<AppPermissionErrorEventArgs>? PermissionDenied;

    public void Raise(FileSystemError error) => PermissionDenied?.Invoke(this, new AppPermissionErrorEventArgs(error));
}
