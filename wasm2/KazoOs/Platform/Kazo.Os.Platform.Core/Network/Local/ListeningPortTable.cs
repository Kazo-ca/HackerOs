using System.Net;
using System.Threading.Channels;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Network.Local;

/// <summary>The process that owns a listening socket.</summary>
/// <param name="Pid">Owner process.</param>
/// <param name="AppId">App of the process.</param>
/// <param name="ProgramName">Short program name, as <c>netstat -p</c> prints it.</param>
/// <param name="UserId">Owner's user id (the <c>uid</c> column of <c>/proc/net/tcp</c> is derived from it).</param>
/// <param name="LoginName">Owner's login name.</param>
public sealed record ListeningSocketOwner(ProcessId Pid, string AppId, string ProgramName, string UserId, string LoginName);

/// <summary>One row of the table, as <c>netstat</c> and <c>/proc/net/tcp</c> show it.</summary>
/// <param name="Address">Bound address: <c>0.0.0.0</c> or <c>127.0.0.1</c>.</param>
/// <param name="Port">Bound port.</param>
/// <param name="Owner">Owner process, or <see langword="null"/> for a simulated system daemon.</param>
/// <param name="Inode">Socket inode number, unique in the table, as in <c>/proc/net/tcp</c>.</param>
/// <param name="Prefixes">HTTP prefixes served on this port by the socket.</param>
public sealed record ListeningSocketRow(IPAddress Address, int Port, ListeningSocketOwner? Owner, long Inode, IReadOnlyList<string> Prefixes);

/// <summary>
/// The OS table of listening TCP ports (ADR 0074, section 2): <c>(address, port) → process</c>, kept
/// in Platform Core next to <see cref="AppNetworkGateway"/>, which is the only way in and out.
/// </summary>
/// <remarks>
/// <para>
/// Several processes may share a port when their HTTP prefixes name different hosts or paths, the
/// way Windows' <c>http.sys</c> lets several sites share port 80: requests are dispatched on the
/// <c>Host</c> header and the longest path prefix, an exact host winning over the strong wildcard
/// <c>+</c>, which wins over the weak wildcard <c>*</c>. The same prefix twice is <c>EADDRINUSE</c>.
/// </para>
/// <para>
/// A socket is released when it is disposed, and when its process ends: rows whose process is no
/// longer active are dropped the next time the table is read, like the kernel closing the file
/// descriptors of a dead process.
/// </para>
/// </remarks>
public sealed class ListeningPortTable
{
    private readonly Lock _gate = new();
    private readonly List<OsHttpListenerSocket> _sockets = [];
    private readonly Func<ProcessId, bool> _isAlive;
    private readonly Func<IEnumerable<int>> _systemDaemonPorts;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextInode = 100_000;
    private int _nextEphemeralPort = 32_768;

    /// <summary>Initializes the table.</summary>
    /// <param name="isAlive">Whether a process is still active; its sockets are dropped otherwise.</param>
    /// <param name="systemDaemonPorts">
    /// Ports held by the simulated system daemons of this machine (the open ports of the simulated
    /// network's <c>localhost</c>, such as <c>sshd</c> on 22): listed by <c>netstat</c> without a
    /// process, and refused to apps with <c>EADDRINUSE</c>. <see langword="null"/> means none.
    /// </param>
    public ListeningPortTable(Func<ProcessId, bool> isAlive, Func<IEnumerable<int>>? systemDaemonPorts = null)
    {
        _isAlive = isAlive ?? throw new ArgumentNullException(nameof(isAlive));
        _systemDaemonPorts = systemDaemonPorts ?? (static () => []);
    }

    /// <summary>Gets how long a request waits for the listening process to answer.</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Opens a socket for <paramref name="bindings"/>, or reports the first prefix already taken.</summary>
    /// <param name="owner">The process that listens.</param>
    /// <param name="bindings">Parsed prefixes.</param>
    /// <param name="conflict">The prefix already served by another socket, when the call fails.</param>
    /// <returns>The socket, or <see langword="null"/> on <c>EADDRINUSE</c>.</returns>
    public IHackHttpListenerSocket? TryOpen(ListeningSocketOwner owner, IReadOnlyList<WebBinding> bindings, out string? conflict)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(bindings);
        conflict = null;
        lock (_gate)
        {
            PruneLocked();
            HashSet<int> daemons = [.. _systemDaemonPorts()];
            foreach (WebBinding binding in bindings)
            {
                if (daemons.Contains(binding.Port))
                {
                    conflict = $"0.0.0.0:{binding.Port}";
                    return null;
                }

                if (_sockets.Any(socket => socket.Bindings.Any(existing => SameEndpoint(existing, binding))))
                {
                    conflict = binding.ToString();
                    return null;
                }
            }

            OsHttpListenerSocket created = new(this, owner, [.. bindings.Distinct()], _nextInode);
            // One kernel socket (and inode) per port, as a server binding two ports opens two sockets.
            _nextInode += created.Ports.Count;
            _sockets.Add(created);
            SignalLocked();
            return created;
        }
    }

    /// <summary>
    /// Finds the socket that serves <paramref name="host"/>:<paramref name="port"/><paramref name="path"/>,
    /// as <c>http.sys</c> dispatches: exact host, then <c>+</c>, then <c>*</c>; longest path prefix.
    /// </summary>
    /// <param name="host">The <c>Host</c> header's name, lower case.</param>
    /// <param name="port">The destination port.</param>
    /// <param name="path">The request path.</param>
    /// <param name="loopbackDestination">Whether the connection goes to 127.0.0.0/8 (a socket bound to loopback only accepts those).</param>
    /// <returns>The socket, or <see langword="null"/> when nothing listens there.</returns>
    internal OsHttpListenerSocket? Route(string host, int port, string path, bool loopbackDestination)
    {
        lock (_gate)
        {
            PruneLocked();
            OsHttpListenerSocket? best = null;
            (int Rank, int Length) bestScore = (-1, -1);
            foreach (OsHttpListenerSocket socket in _sockets)
            {
                if (socket.IsLoopbackOnly && !loopbackDestination)
                {
                    continue;
                }

                foreach (WebBinding binding in socket.Bindings)
                {
                    // "/app" reaches the prefix "/app/" too, as http.sys redirects it there.
                    bool pathMatches = path.StartsWith(binding.Path, StringComparison.Ordinal) || path + "/" == binding.Path;
                    if (binding.Port != port || !pathMatches)
                    {
                        continue;
                    }

                    int rank = binding.Host switch
                    {
                        "*" => 0,
                        "+" => 2,
                        _ when string.Equals(binding.Host, host, StringComparison.OrdinalIgnoreCase) => 3,
                        _ when binding.IsLoopbackHost && WebAppUrls.IsLoopbackName(host) => 1,
                        _ => -1
                    };
                    if (rank < 0)
                    {
                        continue;
                    }

                    (int, int) score = (rank, binding.Path.Length);
                    if (score.CompareTo(bestScore) > 0)
                    {
                        bestScore = score;
                        best = socket;
                    }
                }
            }

            return best;
        }
    }

    /// <summary>Gets whether some socket listens on <paramref name="port"/> (for a port probe).</summary>
    /// <param name="port">The port.</param>
    /// <param name="loopbackDestination">Whether the probe targets 127.0.0.0/8.</param>
    public bool IsListening(int port, bool loopbackDestination)
    {
        lock (_gate)
        {
            PruneLocked();
            return _sockets.Any(socket => (loopbackDestination || !socket.IsLoopbackOnly) && socket.Bindings.Any(binding => binding.Port == port));
        }
    }

    /// <summary>
    /// Gets one row per socket and port, ordered by port, as <c>netstat</c> lists them; the system
    /// daemons come first on their port, bound to every interface and owned by no visible process.
    /// Their inode is their port number, below the inodes of app sockets.
    /// </summary>
    public IReadOnlyList<ListeningSocketRow> Snapshot()
    {
        lock (_gate)
        {
            PruneLocked();
            return
            [
                .. _systemDaemonPorts()
                    .Distinct()
                    .Select(static port => new ListeningSocketRow(IPAddress.Any, port, null, port, []))
                    .Concat(_sockets
                    .SelectMany(socket => socket.Ports.Select((port, index) => new ListeningSocketRow(
                        socket.BoundAddress,
                        port,
                        socket.Owner,
                        socket.Inode + index,
                        [.. socket.Bindings.Where(binding => binding.Port == port).Select(static binding => binding.ToString())]))))
                    .OrderBy(static row => row.Port)
                    .ThenBy(static row => row.Owner?.Pid.Value ?? 0)
            ];
        }
    }

    /// <summary>
    /// Waits until a socket serves <paramref name="host"/>:<paramref name="port"/><paramref name="path"/>,
    /// or <paramref name="timeout"/> elapses (on-demand activation, ADR 0074 section 3).
    /// </summary>
    /// <returns>The socket, or <see langword="null"/> on timeout.</returns>
    internal async Task<OsHttpListenerSocket?> WaitForRouteAsync(
        string host, int port, string path, bool loopbackDestination, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                changed = _changed.Task;
            }

            if (Route(host, port, path, loopbackDestination) is { } socket)
            {
                return socket;
            }

            try
            {
                await changed.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    /// <summary>Takes the next client port of the ephemeral range (32768–60999, as Linux).</summary>
    internal int NextEphemeralPort()
    {
        lock (_gate)
        {
            int port = _nextEphemeralPort;
            _nextEphemeralPort = port >= 60_999 ? 32_768 : port + 1;
            return port;
        }
    }

    /// <summary>Removes a socket closed by its owner.</summary>
    internal void Remove(OsHttpListenerSocket socket)
    {
        lock (_gate)
        {
            if (_sockets.Remove(socket))
            {
                SignalLocked();
            }
        }
    }

    private void PruneLocked()
    {
        List<OsHttpListenerSocket>? dead = null;
        foreach (OsHttpListenerSocket socket in _sockets)
        {
            if (!_isAlive(socket.Owner.Pid))
            {
                (dead ??= []).Add(socket);
            }
        }

        if (dead is null)
        {
            return;
        }

        foreach (OsHttpListenerSocket socket in dead)
        {
            _sockets.Remove(socket);
            socket.Abandon();
        }

        SignalLocked();
    }

    private void SignalLocked()
    {
        TaskCompletionSource previous = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    /// <summary>
    /// Whether two prefixes claim the same thing: same port, same host (wildcards included) and same
    /// path. Two names on one port, or two paths of one name, can live side by side.
    /// </summary>
    private static bool SameEndpoint(WebBinding left, WebBinding right) =>
        left.Port == right.Port
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Path, right.Path, StringComparison.Ordinal);
}

/// <summary>
/// The OS side of one listening socket: requests queue here until the owner accepts them, and each
/// waits for its reply (ADR 0074, section 2).
/// </summary>
internal sealed class OsHttpListenerSocket : IHackHttpListenerSocket
{
    private readonly ListeningPortTable _table;
    private readonly Channel<Pending> _queue = Channel.CreateUnbounded<Pending>(new UnboundedChannelOptions { SingleReader = false });
    private readonly Lock _gate = new();
    private readonly Dictionary<long, Pending> _inFlight = [];
    private long _nextExchange;
    private bool _closed;

    public OsHttpListenerSocket(ListeningPortTable table, ListeningSocketOwner owner, IReadOnlyList<WebBinding> bindings, long inode)
    {
        _table = table;
        Owner = owner;
        Bindings = bindings;
        Inode = inode;
        Prefixes = [.. bindings.Select(static binding => binding.ToString())];
        Ports = [.. bindings.Select(static binding => binding.Port).Distinct().Order()];
        // A socket whose every prefix names this machine's loopback binds 127.0.0.1, like a
        // server configured with "localhost"; anything else binds every interface.
        IsLoopbackOnly = bindings.All(static binding => binding.IsLoopbackHost);
    }

    public ListeningSocketOwner Owner { get; }

    public IReadOnlyList<WebBinding> Bindings { get; }

    public long Inode { get; }

    public bool IsLoopbackOnly { get; }

    public IReadOnlyList<string> Prefixes { get; }

    /// <summary>Gets the distinct ports of <see cref="Bindings"/>, ascending.</summary>
    public IReadOnlyList<int> Ports { get; }

    /// <summary>Gets the address the socket is bound to.</summary>
    public IPAddress BoundAddress => IsLoopbackOnly ? IPAddress.Loopback : IPAddress.Any;

    /// <summary>
    /// Queues a request and waits for the owner's reply; <see langword="null"/> when the socket closed
    /// before answering (the connection was reset) or the owner did not answer in time.
    /// </summary>
    public async Task<HackHttpListenerReply?> ExchangeAsync(
        Func<long, HackHttpListenerExchange> build, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Pending pending;
        lock (_gate)
        {
            if (_closed)
            {
                return null;
            }

            long id = ++_nextExchange;
            pending = new Pending(build(id));
            _inFlight[id] = pending;
        }

        try
        {
            if (!_queue.Writer.TryWrite(pending))
            {
                return null;
            }

            return await pending.Reply.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(pending.Exchange.Id);
            }
        }
    }

    public async ValueTask<HackHttpListenerExchange?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_queue.Reader.TryRead(out Pending? pending) && !pending.Reply.Task.IsCompleted)
            {
                return pending.Exchange;
            }
        }

        return null;
    }

    public void Respond(long exchangeId, HackHttpListenerReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        Pending? pending;
        lock (_gate)
        {
            _inFlight.TryGetValue(exchangeId, out pending);
        }

        pending?.Reply.TrySetResult(reply);
    }

    public ValueTask DisposeAsync()
    {
        _table.Remove(this);
        Abandon();
        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the socket: the queue ends and every request waiting for a reply gets none.</summary>
    public void Abandon()
    {
        List<Pending> waiting;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            waiting = [.. _inFlight.Values];
        }

        _queue.Writer.TryComplete();
        foreach (Pending pending in waiting)
        {
            pending.Reply.TrySetResult(null);
        }
    }

    private sealed class Pending(HackHttpListenerExchange exchange)
    {
        public HackHttpListenerExchange Exchange { get; } = exchange;

        public TaskCompletionSource<HackHttpListenerReply?> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
