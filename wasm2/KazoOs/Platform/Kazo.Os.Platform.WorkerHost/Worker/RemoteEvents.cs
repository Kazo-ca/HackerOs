using System.Text.Json;
using System.Threading.Channels;
using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.WorkerHost.Protocol;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.WorkerHost.Worker;

/// <summary>
/// The subscriptions of one process in a worker (ADR 0124, section 1), by the number the worker gave
/// each one. The bus stays in the OS: it pushes every delivery as an <c>ev</c> message, which this
/// table hands to the right subscription, in order, then acknowledges (<c>ev.ack</c>) so the OS can
/// send more, like the window of a TCP connection.
/// </summary>
internal sealed class RemoteEventTable
{
    private readonly RemoteProcessChannel _channel;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Action<JsonElement>> _subscriptions = [];
    private Func<IDisposable>? _enterAmbient;
    private Action<string>? _logError;
    private int _nextId;
    private bool _closed;

    public RemoteEventTable(RemoteProcessChannel channel) => _channel = channel;

    /// <summary>Gets the number of live subscriptions (tests and diagnostics).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _subscriptions.Count;
            }
        }
    }

    /// <summary>Binds the process context: deliveries run in its ambient frame and handler faults go to its log.</summary>
    public void Bind(Func<IDisposable> enterAmbient, Action<string> logError)
    {
        _enterAmbient = enterAmbient;
        _logError = logError;
    }

    /// <summary>Registers a subscription before the OS is told about it, so no early delivery is lost.</summary>
    /// <returns>The subscription's number, carried by every delivery.</returns>
    /// <exception cref="ObjectDisposedException">The process has ended.</exception>
    public int Add(Action<JsonElement> deliver)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            int id = ++_nextId;
            _subscriptions[id] = deliver;
            return id;
        }
    }

    /// <summary>Forgets a subscription; deliveries already on their way are dropped.</summary>
    /// <returns>Whether it was still registered.</returns>
    public bool Remove(int id)
    {
        lock (_gate)
        {
            return _subscriptions.Remove(id);
        }
    }

    /// <summary>Hands one <c>ev</c> message to its subscription, then returns its credit to the OS.</summary>
    public void Deliver(int id, JsonElement message)
    {
        Action<JsonElement>? deliver;
        lock (_gate)
        {
            _subscriptions.TryGetValue(id, out deliver);
        }

        if (deliver is not null)
        {
            using IDisposable? ambient = _enterAmbient?.Invoke();
            try
            {
                deliver(message);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A faulty handler must not stop the others, as on the main thread (EventDispatchFault);
                // nobody waits for its result here, so the fault goes to the app's log.
                _logError?.Invoke($"An event handler threw {exception.GetType().Name}: {exception.Message}");
            }
        }

        try
        {
            _channel.Notify(RemoteOps.EventAcknowledge, writer =>
            {
                writer.WriteNumber("s", id);
                writer.WriteNumber("n", 1);
            });
        }
        catch (Exception exception) when (exception is Kazo.Os.AppSdk.Diagnostics.HackProcessKilledException or InvalidOperationException)
        {
            // The process is going away: the OS drops its subscriptions itself.
        }
    }

    /// <summary>The process ended or was killed: nothing is delivered any more.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _subscriptions.Clear();
        }
    }

    /// <summary>Tells the OS a subscription ended (dispose), unless the process is already gone.</summary>
    public void Unsubscribe(int id)
    {
        if (!Remove(id))
        {
            return;
        }

        try
        {
            _channel.Notify(RemoteOps.EventUnsubscribe, writer => writer.WriteNumber("s", id));
        }
        catch (Exception exception) when (exception is Kazo.Os.AppSdk.Diagnostics.HackProcessKilledException or InvalidOperationException)
        {
            // Same as above.
        }
    }

    /// <summary>
    /// Sends the <c>ev.sub</c> or <c>watch.open</c> call without waiting for it (the SDK's subscribe is
    /// synchronous and a worker's single thread cannot block): a refusal arrives later and is handed
    /// to <paramref name="refused"/>, after the subscription is forgotten.
    /// </summary>
    public void Open(int id, string operation, Action<Utf8JsonWriter> arguments, Action<Exception> refused)
    {
        _ = OpenAsync();

        async Task OpenAsync()
        {
            try
            {
                await _channel.CallAsync(operation, arguments, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (Remove(id))
                {
                    refused(exception);
                }
            }
        }
    }

    /// <summary>Writes the reason of a refused subscription to the app's log.</summary>
    public void LogRefusal(string what, Exception exception) =>
        _logError?.Invoke($"The OS refused {what}: {exception.Message}");
}

/// <summary>
/// <see cref="IAppEventGateway"/> in a worker (ADR 0124, sections 1 and 2): every subscription is made
/// by the OS on behalf of this process, with its rights, and every delivery arrives as a message.
/// Payloads travel as JSON (<see cref="WirePayloadCodec"/>); a type that cannot cross fails here with a
/// <see cref="NotSupportedException"/> naming the attribute to add.
/// </summary>
/// <remarks>
/// <para>
/// The SDK's members are synchronous and a worker's single thread cannot wait for the OS, so (ADR 0138):
/// a subscription returns at once and a refusal arrives later (a channel completes with the error, a
/// callback subscription writes it to the app's log); <c>Publish</c> posts the message in order with
/// the process's other calls and returns <see cref="TopicPublishOutcome.Delivered"/> with no
/// subscriber faults. On its own namespace this is exactly the OS's answer; on a shared channel, the
/// OS writes a refusal to the app's log.
/// </para>
/// <para>
/// Deliveries are asynchronous: a message this process publishes reaches its own subscriptions after
/// <c>Publish</c> returns, not during it as on the main thread.
/// </para>
/// </remarks>
internal sealed class RemoteEventGateway(RemoteProcessChannel channel, RemoteEventTable table) : IAppEventGateway
{
    /// <summary>Kind of an <c>ev.sub</c>: a topic of the app lane.</summary>
    public const string TopicKind = "t";

    /// <summary>Kind of an <c>ev.sub</c>: a typed event of the kernel lane.</summary>
    public const string EventKind = "e";

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureCarried(typeof(TEvent));
        int id = table.Add(message => handler((TEvent)WirePayloadCodec.Deserialize(typeof(TEvent), message.GetString("j"))));
        table.Open(id, RemoteOps.EventSubscribe, writer =>
        {
            writer.WriteNumber("s", id);
            writer.WriteString("k", EventKind);
            writer.WriteString("w", WirePayloadNames.Of(typeof(TEvent)));
        }, exception => table.LogRefusal($"the subscription to {typeof(TEvent).Name}", exception));
        return new Subscription(table, id);
    }

    /// <summary>No CLR event type is app-publishable (see the interface): denied silently, as on the main thread.</summary>
    public IReadOnlyList<EventDispatchFault> Publish<TEvent>(TEvent @event) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(@event);
        return [];
    }

    public IDisposable Subscribe<TPayload>(TopicName topic, Action<TopicMessage<TPayload>> handler) where TPayload : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureCarried(typeof(TPayload));
        int id = table.Add(message => handler(ReadMessage<TPayload>(message)));
        table.Open(id, RemoteOps.EventSubscribe, writer => WriteTopicSubscription<TPayload>(writer, id, topic, boundedCapacity: null),
            exception => table.LogRefusal($"the subscription to {topic.Value}", exception));
        return new Subscription(table, id);
    }

    public ITopicChannelSubscription<TPayload> SubscribeChannel<TPayload>(TopicName topic, int? boundedCapacity = null) where TPayload : notnull
    {
        EnsureCarried(typeof(TPayload));
        Channel<TopicMessage<TPayload>> queue = CreateChannel<TPayload>(boundedCapacity);
        int id = table.Add(message => queue.Writer.TryWrite(ReadMessage<TPayload>(message)));
        // A refusal completes the channel with the error: the reader sees it, as it would have seen
        // the exception of the subscribe call on the main thread.
        table.Open(id, RemoteOps.EventSubscribe, writer => WriteTopicSubscription<TPayload>(writer, id, topic, boundedCapacity),
            exception => queue.Writer.TryComplete(exception));
        return new ChannelSubscription<TPayload>(table, id, queue);
    }

    public TopicPublishResult Publish<TPayload>(TopicName topic, TPayload payload) where TPayload : notnull
    {
        ArgumentNullException.ThrowIfNull(payload);
        string json = WirePayloadCodec.Serialize(typeof(TPayload), payload);
        channel.Notify(RemoteOps.EventPublish, writer =>
        {
            writer.WriteString("top", topic.Value);
            writer.WriteString("w", WirePayloadNames.Of(typeof(TPayload)));
            writer.WriteString("j", json);
        });
        return new TopicPublishResult(TopicPublishOutcome.Delivered, []);
    }

    /// <summary>
    /// The publication as a call (ADR 0145): the OS publishes on behalf of this process and answers
    /// with its real outcome and the faults of its subscribers, rebuilt here as exceptions.
    /// </summary>
    public async Task<TopicPublishResult> PublishAsync<TPayload>(TopicName topic, TPayload payload, CancellationToken cancellationToken = default)
        where TPayload : notnull
    {
        ArgumentNullException.ThrowIfNull(payload);
        string json = WirePayloadCodec.Serialize(typeof(TPayload), payload);
        JsonElement value = await channel.CallAsync(RemoteOps.EventPublishWithResult, writer =>
        {
            writer.WriteString("top", topic.Value);
            writer.WriteString("w", WirePayloadNames.Of(typeof(TPayload)));
            writer.WriteString("j", json);
        }, cancellationToken).ConfigureAwait(false);
        List<EventDispatchFault> faults = [];
        if (value.TryGetProperty("f", out JsonElement reported))
        {
            foreach (JsonElement fault in reported.EnumerateArray())
            {
                faults.Add(new EventDispatchFault(typeof(TPayload), RemoteErrors.Read(fault.GetProperty("e"))));
            }
        }

        return new TopicPublishResult((TopicPublishOutcome)value.GetProperty("o").GetInt32(), faults);
    }

    public void RegisterSharedChannel(TopicName root, SharedChannelPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.ResourceScope is not null and not VirtualPathResourceCandidate)
        {
            throw RemoteUnsupported.Member($"IAppEventGateway.RegisterSharedChannel with a {policy.ResourceScope.Kind} resource scope");
        }

        channel.Notify(RemoteOps.EventSharedChannel, writer =>
        {
            writer.WriteString("top", root.Value);
            writer.WriteNumber("pa", (int)policy.PublishAccess);
            writer.WriteNumber("sa", (int)policy.SubscribeAccess);
            if (policy.PublishCapability is { } publish)
            {
                writer.WriteString("pc", publish);
            }

            if (policy.SubscribeCapability is { } subscribe)
            {
                writer.WriteString("sc", subscribe);
            }

            if (policy.ResourceScope is VirtualPathResourceCandidate path)
            {
                writer.WriteString("rp", path.Path.Value);
            }
        });
    }

    /// <summary>Reads a <c>ev.chan</c> policy back on the OS side.</summary>
    public static SharedChannelPolicy ReadPolicy(JsonElement arguments) => new(
        (SharedChannelAccessMode)arguments.GetInt32("pa"),
        (SharedChannelAccessMode)arguments.GetInt32("sa"),
        arguments.GetOptionalString("pc"),
        arguments.GetOptionalString("sc"),
        arguments.GetOptionalString("rp") is { } path ? new VirtualPathResourceCandidate(VirtualPath.Parse(path)) : null);

    /// <summary>Reads one topic delivery: the message the OS's bus built, with a payload rebuilt in this runtime.</summary>
    public static TopicMessage<TPayload> ReadMessage<TPayload>(JsonElement message) where TPayload : notnull => new(
        WirePayloadCodec.ParseTopic(message.GetString("top")),
        (TPayload)WirePayloadCodec.Deserialize(typeof(TPayload), message.GetString("j")),
        message.GetString("pa"),
        WireCodec.ReadTime(message, "at"));

    /// <summary>The same channel the OS's bus creates (bounded ones drop the oldest message).</summary>
    public static Channel<TopicMessage<TPayload>> CreateChannel<TPayload>(int? boundedCapacity) where TPayload : notnull =>
        boundedCapacity is { } capacity
            ? Channel.CreateBounded<TopicMessage<TPayload>>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest })
            : Channel.CreateUnbounded<TopicMessage<TPayload>>();

    private static void WriteTopicSubscription<TPayload>(Utf8JsonWriter writer, int id, TopicName topic, int? boundedCapacity)
    {
        writer.WriteNumber("s", id);
        writer.WriteString("k", TopicKind);
        writer.WriteString("top", topic.Value);
        writer.WriteString("w", WirePayloadNames.Of(typeof(TPayload)));
        if (boundedCapacity is { } capacity)
        {
            writer.WriteNumber("c", capacity);
        }
    }

    /// <summary>Fails at once, in the app, for a payload type that cannot cross (ADR 0124, section 2).</summary>
    private static void EnsureCarried(Type type)
    {
        if (!WirePayloadCodec.CanCarry(type))
        {
            throw WirePayloadCodec.Unsupported(type);
        }
    }

    private sealed class Subscription(RemoteEventTable table, int id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                table.Unsubscribe(id);
            }
        }
    }
}

/// <summary>A channel subscription in a worker: disposing it unsubscribes in the OS and completes the channel.</summary>
internal sealed class ChannelSubscription<TPayload>(RemoteEventTable table, int id, Channel<TopicMessage<TPayload>> queue)
    : ITopicChannelSubscription<TPayload> where TPayload : notnull
{
    private int _disposed;

    public ChannelReader<TopicMessage<TPayload>> Reader => queue.Reader;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            table.Unsubscribe(id);
            queue.Writer.TryComplete();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// <see cref="IAppFileSystemWatchGateway"/> in a worker (ADR 0124, section 3): the OS opens the watch
/// on behalf of this process, with its read rights, and pushes each change through the same channel
/// as the events. Unlike a subscription, opening it waits for the OS's answer, so a refusal is thrown.
/// </summary>
internal sealed class RemoteWatchGateway(RemoteProcessChannel channel, RemoteEventTable table) : IAppFileSystemWatchGateway
{
    public async ValueTask<ITopicChannelSubscription<FileSystemChangeEvent>> WatchAsync(
        VirtualPath path, FileSystemWatchScope scope, CancellationToken cancellationToken = default)
    {
        Channel<TopicMessage<FileSystemChangeEvent>> queue = Channel.CreateUnbounded<TopicMessage<FileSystemChangeEvent>>();
        int id = table.Add(message => queue.Writer.TryWrite(RemoteEventGateway.ReadMessage<FileSystemChangeEvent>(message)));
        try
        {
            await channel.CallAsync(RemoteOps.WatchOpen, writer =>
            {
                writer.WriteNumber("s", id);
                writer.WriteString("p", path.Value);
                writer.WriteNumber("sc", (int)scope);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cancelled or refused: if the OS opened it anyway, the unsubscribe closes it there too.
            table.Unsubscribe(id);
            throw;
        }

        return new ChannelSubscription<FileSystemChangeEvent>(table, id, queue);
    }
}
