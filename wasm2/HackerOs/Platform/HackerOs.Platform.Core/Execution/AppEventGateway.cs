using HackerOs.Platform.Core.Events;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.Platform.Core.Execution;

/// <summary>
/// Provides one app instance's typed event bus access: read-only pass-through to the kernel-lane
/// <see cref="IEventBus"/>, and authorized publish/subscribe through the app-lane
/// <see cref="ITopicMessageBus"/>, stamping this instance's own trusted identity as publisher/subscriber.
/// See <c>doc/en/wasm/dev/adr/0038-emitter-authorized-topic-messaging.md</c>.
/// </summary>
public sealed class AppEventGateway : IAppEventGateway
{
    private readonly IEventBus _eventBus;
    private readonly ITopicMessageBus _topicBus;
    private readonly PublisherIdentity _identity;
    private readonly AppAmbientContextBinding _ambient = new();

    /// <summary>Initializes an event gateway bound to the shared event/topic buses and this app instance's identity.</summary>
    public AppEventGateway(IEventBus eventBus, ITopicMessageBus topicBus, string appId, string userId, string processId)
    {
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _topicBus = topicBus ?? throw new ArgumentNullException(nameof(topicBus));
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        _identity = new PublisherIdentity(appId, userId, processId);
    }

    /// <inheritdoc />
    /// <remarks>The handler runs inside this app's <see cref="HackerOs.AppSdk.HackExecutionContext"/> scope (ADR 0043).</remarks>
    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull =>
        _eventBus.Subscribe(_ambient.Wrap(handler));

    /// <inheritdoc />
    public IReadOnlyList<EventDispatchFault> Publish<TEvent>(TEvent @event) where TEvent : notnull
    {
        // No CLR event type is currently app-publishable — see the interface remarks. Denied silently
        // (no delivery attempted) rather than throwing, matching the existing fault-isolation convention.
        ArgumentNullException.ThrowIfNull(@event);
        return [];
    }

    /// <inheritdoc />
    /// <remarks>The handler runs inside this app's <see cref="HackerOs.AppSdk.HackExecutionContext"/> scope (ADR 0043).</remarks>
    public IDisposable Subscribe<TPayload>(TopicName topic, Action<TopicMessage<TPayload>> handler) where TPayload : notnull =>
        _topicBus.Subscribe(topic, _identity, _ambient.Wrap(handler));

    /// <inheritdoc />
    public ITopicChannelSubscription<TPayload> SubscribeChannel<TPayload>(TopicName topic, int? boundedCapacity = null)
        where TPayload : notnull =>
        _topicBus.SubscribeChannel<TPayload>(topic, _identity, boundedCapacity);

    /// <inheritdoc />
    public TopicPublishResult Publish<TPayload>(TopicName topic, TPayload payload) where TPayload : notnull =>
        _topicBus.Publish(topic, payload, _identity);

    /// <inheritdoc />
    public void RegisterSharedChannel(TopicName root, SharedChannelPolicy policy) =>
        _topicBus.RegisterSharedChannel(root, policy, _identity);

    /// <summary>
    /// Subscribes, on behalf of this process running in a Web Worker, to <paramref name="topic"/> for
    /// payloads of the wire type <paramref name="payloadWireName"/> (ADR 0124, section 1), with this
    /// instance's identity and therefore its rights.
    /// </summary>
    /// <exception cref="NotSupportedException">The OS's topic bus does not serve workers.</exception>
    internal IDisposable SubscribeWire(TopicName topic, string payloadWireName, Action<WireTopicDelivery> handler) =>
        WireTopics.SubscribeWire(topic, _identity, payloadWireName, handler);

    /// <summary>Publishes a payload this process serialized in its worker (ADR 0124, section 1).</summary>
    /// <exception cref="NotSupportedException">The OS's topic bus does not serve workers.</exception>
    internal TopicPublishResult PublishWire(TopicName topic, string payloadWireName, string json, Func<Type, object> materialize) =>
        WireTopics.PublishWire(topic, payloadWireName, json, materialize, _identity);

    /// <summary>Subscribes, on behalf of this process running in a Web Worker, to a kernel event type by wire name.</summary>
    /// <exception cref="NotSupportedException">The OS's event bus does not serve workers.</exception>
    internal IDisposable SubscribeEventWire(string eventWireName, Action<Type, object> handler) =>
        (_eventBus as IWireEventBus ?? throw new NotSupportedException("This OS's event bus does not serve processes in workers."))
            .SubscribeWire(eventWireName, handler);

    private IWireTopicMessageBus WireTopics =>
        _topicBus as IWireTopicMessageBus ?? throw new NotSupportedException("This OS's topic bus does not serve processes in workers.");

    /// <summary>Binds the app context subscription handlers run under; called once by the factory.</summary>
    internal void BindAmbientContext(HackerOs.AppSdk.IAppExecutionContext context) => _ambient.Bind(context);
}
