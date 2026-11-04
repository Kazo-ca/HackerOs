using System.Threading.Channels;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.WorkerHost.Protocol;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.WorkerHost.Host;

/// <summary>
/// The OS side of the subscriptions of one process in a worker (ADR 0124, section 1): each one is a
/// real subscription of the OS's bus, made on behalf of the process; what it receives is queued here
/// and posted to the worker as <c>ev</c> messages, at most <see cref="Credit"/> in flight until the
/// worker acknowledges them (<c>ev.ack</c>), like the receive window of a socket.
/// </summary>
/// <remarks>
/// The bus calls a subscription synchronously from the publisher; queuing never blocks it. A bounded
/// subscription keeps its capacity here too and drops the oldest message when full, the rule of the
/// bus's own channels; a callback subscription has an unbounded queue and waits for the worker's credit.
/// </remarks>
internal sealed class RemoteEventRouter
{
    /// <summary>Messages in flight per subscription before the OS waits for the worker's acknowledgement.</summary>
    public const int Credit = 256;

    private readonly long _pid;
    private readonly Action<string> _post;
    private readonly Action<string> _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Route> _routes = [];
    private bool _closed;

    /// <summary>Initializes the router of one process.</summary>
    /// <param name="pid">The process.</param>
    /// <param name="post">Posts a message to its worker.</param>
    /// <param name="log">Writes a diagnostic line (the worker pool's log).</param>
    public RemoteEventRouter(long pid, Action<string> post, Action<string> log)
    {
        _pid = pid;
        _post = post;
        _log = log;
    }

    /// <summary>Gets the number of open subscriptions (tests and diagnostics).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _routes.Count;
            }
        }
    }

    /// <summary>
    /// Opens subscription <paramref name="id"/>: the route exists before <paramref name="subscribe"/>
    /// runs, so a message published while it subscribes is not lost.
    /// </summary>
    /// <param name="id">The worker's number for the subscription.</param>
    /// <param name="boundedCapacity">The capacity of a bounded channel subscription, or <see langword="null"/>.</param>
    /// <param name="subscribe">Subscribes on the bus with the given enqueue function; returns the handle to dispose.</param>
    /// <exception cref="InvalidOperationException">The process has ended, or the number is in use.</exception>
    public void Open(int id, int? boundedCapacity, Func<Action<string>, IDisposable> subscribe)
    {
        Route route = AddRoute(id, boundedCapacity);
        try
        {
            Attach(route, subscribe(route.Enqueue));
        }
        catch
        {
            Close(id);
            throw;
        }
    }

    /// <summary>
    /// Opens subscription <paramref name="id"/> over a directory watch the OS already opened for the
    /// process (ADR 0124, section 3): each change of its channel is queued for the worker.
    /// </summary>
    public void OpenWatch(int id, ITopicChannelSubscription<FileSystemChangeEvent> watch)
    {
        Route route;
        try
        {
            route = AddRoute(id, boundedCapacity: null);
        }
        catch
        {
            _ = watch.DisposeAsync();
            throw;
        }

        Attach(route, new WatchHandle(watch));
        _ = PumpWatchAsync(route, watch);
    }

    /// <summary>Returns <paramref name="count"/> credits of subscription <paramref name="id"/>.</summary>
    public void Acknowledge(int id, int count)
    {
        Route? route;
        lock (_gate)
        {
            _routes.TryGetValue(id, out route);
        }

        route?.Acknowledge(count);
    }

    /// <summary>Ends one subscription (the worker disposed it): unsubscribes from the bus at once.</summary>
    public void Close(int id)
    {
        Route? route;
        lock (_gate)
        {
            _routes.Remove(id, out route);
        }

        route?.Dispose();
    }

    /// <summary>
    /// The process ended, was killed or lost its worker: every subscription is dropped from the bus
    /// without waiting for the worker (ADR 0124, section 1).
    /// </summary>
    public void CloseAll()
    {
        List<Route> routes;
        lock (_gate)
        {
            _closed = true;
            routes = [.. _routes.Values];
            _routes.Clear();
        }

        foreach (Route route in routes)
        {
            route.Dispose();
        }
    }

    /// <summary>Builds the <c>ev</c> message of a topic delivery.</summary>
    public string TopicMessage(int id, WireTopicDelivery delivery)
    {
        string json = delivery.Json ?? WirePayloadCodec.Serialize(delivery.PayloadType!, delivery.Payload!);
        return WorkerMessage.Create(WorkerMessageType.Event, writer =>
        {
            writer.WriteNumber("p", _pid);
            writer.WriteNumber("s", id);
            writer.WriteString("j", json);
            writer.WriteString("top", delivery.Topic.Value);
            writer.WriteString("pa", delivery.PublisherAppId);
            WireCodec.WriteTime(writer, "at", delivery.PublishedAtUtc);
        });
    }

    /// <summary>Builds the <c>ev</c> message of a typed (kernel) event.</summary>
    public string EventMessage(int id, Type eventType, object @event)
    {
        string json = WirePayloadCodec.Serialize(eventType, @event);
        return WorkerMessage.Create(WorkerMessageType.Event, writer =>
        {
            writer.WriteNumber("p", _pid);
            writer.WriteNumber("s", id);
            writer.WriteString("j", json);
        });
    }

    private Route AddRoute(int id, int? boundedCapacity)
    {
        Route route = new(this, id, boundedCapacity);
        lock (_gate)
        {
            if (_closed)
            {
                throw new InvalidOperationException($"Process {_pid} has ended.");
            }

            if (!_routes.TryAdd(id, route))
            {
                throw new InvalidOperationException($"Subscription {id} of process {_pid} is already open.");
            }
        }

        route.Start();
        return route;
    }

    private void Attach(Route route, IDisposable handle)
    {
        bool open;
        lock (_gate)
        {
            open = _routes.ContainsValue(route);
        }

        route.Attach(handle);
        if (!open)
        {
            route.Dispose(); // Closed while it subscribed: the handle goes at once.
        }
    }

    private async Task PumpWatchAsync(Route route, ITopicChannelSubscription<FileSystemChangeEvent> watch)
    {
        try
        {
            await foreach (TopicMessage<FileSystemChangeEvent> change in watch.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                route.Enqueue(TopicMessage(route.Id, new WireTopicDelivery(
                    change.Topic, change.PublisherAppId, change.PublishedAtUtc, typeof(FileSystemChangeEvent), change.Payload, Json: null)));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log($"pid {_pid}: watch {route.Id} failed: {exception.Message}");
        }
    }

    /// <summary>One subscription: its bus handle, its queue and the pump that posts within the credit.</summary>
    private sealed class Route
    {
        private readonly RemoteEventRouter _router;
        private readonly Channel<string> _queue;
        private readonly SemaphoreSlim _credit = new(Credit, Credit);
        private readonly CancellationTokenSource _stop = new();
        private IDisposable? _handle;
        private int _disposed;

        public Route(RemoteEventRouter router, int id, int? boundedCapacity)
        {
            _router = router;
            Id = id;
            _queue = boundedCapacity is { } capacity
                ? Channel.CreateBounded<string>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true })
                : Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        }

        /// <summary>Gets the worker's number for the subscription.</summary>
        public int Id { get; }

        /// <summary>Starts posting queued messages within the credit.</summary>
        public void Start() => _ = PumpAsync();

        public void Attach(IDisposable handle) => _handle = handle;

        /// <summary>Queues one message; called by the bus from the publisher, never blocks.</summary>
        public void Enqueue(string message) => _queue.Writer.TryWrite(message);

        public void Acknowledge(int count)
        {
            for (int i = 0; i < count && _credit.CurrentCount < Credit; i++)
            {
                _credit.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _queue.Writer.TryComplete();
            _stop.Cancel();
            try
            {
                _handle?.Dispose();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _router._log($"pid {_router._pid}: unsubscribe {Id} failed: {exception.Message}");
            }
        }

        private async Task PumpAsync()
        {
            try
            {
                await foreach (string message in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                {
                    await _credit.WaitAsync(_stop.Token).ConfigureAwait(false);
                    _router._post(message);
                }
            }
            catch (OperationCanceledException)
            {
                // Closed.
            }
        }
    }

    /// <summary>Disposes a watch's channel subscription synchronously (it completes at once).</summary>
    private sealed class WatchHandle(ITopicChannelSubscription<FileSystemChangeEvent> watch) : IDisposable
    {
        public void Dispose() => _ = watch.DisposeAsync();
    }
}
