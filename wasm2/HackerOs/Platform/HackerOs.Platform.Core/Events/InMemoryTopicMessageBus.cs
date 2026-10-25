using System.Threading.Channels;
using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.Platform.Core.Events;

/// <summary>
/// In-memory <see cref="ITopicMessageBus"/> implementation. Enforces the authorization decisions
/// described in <c>doc/wasm/dev/adr/0038-emitter-authorized-topic-messaging.md</c>: a publisher may always
/// publish within its own app namespace; acting on a shared channel it does not own is governed,
/// independently per direction, by that channel's declared <see cref="SharedChannelAccessMode"/> — a
/// permission requirement is entirely optional per channel, chosen by the owner at registration time
/// (<see cref="SharedChannelAccessMode.Open"/> for none, <see cref="SharedChannelAccessMode.OwnerOnly"/>
/// for none beyond ownership, or <see cref="SharedChannelAccessMode.RequiresCapability"/> for an active
/// capability grant). The channel owner always bypasses every restriction on its own channel.
/// </summary>
/// <remarks>
/// Every acting app/user in this version is assumed to hold <see cref="AppAuthority.User"/> — per
/// `P1-POL-007`, app code never legitimately acts with elevated authority, so evaluating capability
/// grants with a fixed <see cref="AppAuthority.User"/> acting/required authority is correct for every
/// caller this bus currently has (app-facing gateways). Kernel-only channel ownership (e.g. the
/// filesystem-change channel added in Phase 2) is enforced by exact <see cref="PublisherIdentity.AppId"/>
/// ownership matching, not by a capability grant, so it does not depend on this assumption.
/// </remarks>
public sealed class InMemoryTopicMessageBus : ITopicMessageBus, IWireTopicMessageBus
{
    private readonly Lock _gate = new();
    private readonly ICapabilityGrantRepository _grantRepository;
    private readonly Dictionary<string, List<Subscription>> _subscriptionsByTopic = [];
    private readonly Dictionary<string, SharedChannelRegistration> _sharedChannelsByRoot = [];

    /// <summary>Initializes a topic bus that evaluates shared-channel policy against the trusted grant repository.</summary>
    public InMemoryTopicMessageBus(ICapabilityGrantRepository grantRepository)
    {
        _grantRepository = grantRepository ?? throw new ArgumentNullException(nameof(grantRepository));
    }

    /// <inheritdoc />
    public IDisposable Subscribe<TPayload>(TopicName topic, PublisherIdentity subscriber, Action<TopicMessage<TPayload>> handler)
        where TPayload : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureSubscribeAllowed(topic.Value, subscriber);

        Subscription subscription = Subscription.Local<TPayload>(handler);
        return Add(topic.Value, subscription);
    }

    /// <inheritdoc />
    IDisposable IWireTopicMessageBus.SubscribeWire(
        TopicName topic, PublisherIdentity subscriber, string payloadWireName, Action<WireTopicDelivery> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadWireName);
        ArgumentNullException.ThrowIfNull(handler);
        EnsureSubscribeAllowed(topic.Value, subscriber);
        return Add(topic.Value, Subscription.Wire(payloadWireName, handler));
    }

    /// <inheritdoc />
    int IWireTopicMessageBus.CountSubscriptions(TopicName topic)
    {
        lock (_gate)
        {
            return _subscriptionsByTopic.TryGetValue(topic.Value, out List<Subscription>? subscriptions) ? subscriptions.Count : 0;
        }
    }

    private Unsubscriber Add(string topicValue, Subscription subscription)
    {
        lock (_gate)
        {
            if (!_subscriptionsByTopic.TryGetValue(topicValue, out List<Subscription>? subscriptions))
            {
                subscriptions = [];
                _subscriptionsByTopic[topicValue] = subscriptions;
            }

            subscriptions.Add(subscription);
        }

        return new Unsubscriber(this, topicValue, subscription);
    }

    /// <inheritdoc />
    public ITopicChannelSubscription<TPayload> SubscribeChannel<TPayload>(
        TopicName topic, PublisherIdentity subscriber, int? boundedCapacity = null)
        where TPayload : notnull
    {
        Channel<TopicMessage<TPayload>> channel = boundedCapacity is { } capacity
            ? Channel.CreateBounded<TopicMessage<TPayload>>(
                new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest })
            : Channel.CreateUnbounded<TopicMessage<TPayload>>();

        // Authorization happens inside Subscribe (thrown before the channel is ever handed back), so a
        // denied caller never receives a channel that silently delivers nothing.
        IDisposable subscription = Subscribe<TPayload>(topic, subscriber, message => channel.Writer.TryWrite(message));
        return new ChannelSubscription<TPayload>(subscription, channel);
    }

    /// <inheritdoc />
    public TopicPublishResult Publish<TPayload>(TopicName topic, TPayload payload, PublisherIdentity publisher)
        where TPayload : notnull
    {
        ArgumentNullException.ThrowIfNull(payload);

        TopicPublishOutcome? denial = EvaluatePublishDenial(topic.Value, publisher);
        if (denial is { } deniedOutcome)
        {
            return new TopicPublishResult(deniedOutcome, []);
        }

        List<Subscription> snapshot;
        lock (_gate)
        {
            snapshot = _subscriptionsByTopic.TryGetValue(topic.Value, out List<Subscription>? subscriptions)
                ? [.. subscriptions]
                : [];
        }

        TopicMessage<TPayload> message = new(topic, payload, publisher.AppId, DateTimeOffset.UtcNow);
        string? wireName = null;

        List<EventDispatchFault>? faults = null;
        foreach (Subscription subscription in snapshot)
        {
            try
            {
                if (subscription.PayloadType is { } payloadType)
                {
                    // A topic string carries no compile-time payload type; skip a subscriber that requested a
                    // different TPayload for the same topic rather than risk an invalid cast, mirroring how
                    // InMemoryEventBus dispatches strictly by CLR type.
                    if (payloadType == typeof(TPayload))
                    {
                        subscription.Invoke!(message);
                    }
                }
                else if (subscription.WireName == (wireName ??= WirePayloadNames.Of(typeof(TPayload))))
                {
                    // ADR 0124: a subscriber in a worker gets the object; its side of the protocol
                    // serializes it, with the codec its own runtime will use to read it back.
                    subscription.WireInvoke!(new WireTopicDelivery(
                        topic, message.PublisherAppId, message.PublishedAtUtc, typeof(TPayload), payload, Json: null));
                }
            }
            catch (Exception exception)
            {
                faults ??= [];
                faults.Add(new EventDispatchFault(typeof(TPayload), exception));
            }
        }

        return new TopicPublishResult(TopicPublishOutcome.Delivered, faults ?? (IReadOnlyList<EventDispatchFault>)[]);
    }

    /// <inheritdoc />
    TopicPublishResult IWireTopicMessageBus.PublishWire(
        TopicName topic, string payloadWireName, string json, Func<Type, object> materialize, PublisherIdentity publisher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadWireName);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(materialize);

        TopicPublishOutcome? denial = EvaluatePublishDenial(topic.Value, publisher);
        if (denial is { } deniedOutcome)
        {
            return new TopicPublishResult(deniedOutcome, []);
        }

        List<Subscription> snapshot;
        lock (_gate)
        {
            snapshot = _subscriptionsByTopic.TryGetValue(topic.Value, out List<Subscription>? subscriptions)
                ? [.. subscriptions]
                : [];
        }

        DateTimeOffset publishedAt = DateTimeOffset.UtcNow;
        Dictionary<Type, object>? materialized = null;
        List<EventDispatchFault>? faults = null;
        foreach (Subscription subscription in snapshot)
        {
            if (!string.Equals(subscription.WireName, payloadWireName, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (subscription.PayloadType is { } payloadType)
                {
                    // A subscriber on this thread gets a real object of its own payload type, rebuilt
                    // once per type from the JSON, as a reader of a pipe parses what it reads.
                    materialized ??= [];
                    if (!materialized.TryGetValue(payloadType, out object? payload))
                    {
                        payload = materialize(payloadType);
                        materialized[payloadType] = payload;
                    }

                    subscription.Invoke!(subscription.CreateMessage!(topic, payload, publisher.AppId, publishedAt));
                }
                else
                {
                    subscription.WireInvoke!(new WireTopicDelivery(topic, publisher.AppId, publishedAt, PayloadType: null, Payload: null, json));
                }
            }
            catch (Exception exception)
            {
                faults ??= [];
                faults.Add(new EventDispatchFault(subscription.PayloadType ?? typeof(object), exception));
            }
        }

        return new TopicPublishResult(TopicPublishOutcome.Delivered, faults ?? (IReadOnlyList<EventDispatchFault>)[]);
    }

    /// <inheritdoc />
    public void RegisterSharedChannel(TopicName root, SharedChannelPolicy policy, PublisherIdentity owner)
    {
        ArgumentNullException.ThrowIfNull(policy);

        lock (_gate)
        {
            if (_sharedChannelsByRoot.TryGetValue(root.Value, out SharedChannelRegistration? existing)
                && !string.Equals(existing.OwnerAppId, owner.AppId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Shared channel '{root.Value}' is already owned by app '{existing.OwnerAppId}'.");
            }

            _sharedChannelsByRoot[root.Value] = new SharedChannelRegistration(policy, owner.AppId);
        }
    }

    /// <summary>Returns the denial outcome for a publish attempt, or <see langword="null"/> when it is allowed.</summary>
    private TopicPublishOutcome? EvaluatePublishDenial(string topicValue, PublisherIdentity publisher)
    {
        if (TopicNames.IsOwnedByApp(topicValue, publisher.AppId))
        {
            return null;
        }

        SharedChannelRegistration? registration = FindGoverningSharedChannel(topicValue);
        if (registration is null)
        {
            return TopicPublishOutcome.TopicNotOwnedByCaller;
        }

        if (registration.Policy.PublishAccess == SharedChannelAccessMode.Open
            || string.Equals(registration.OwnerAppId, publisher.AppId, StringComparison.Ordinal))
        {
            return null;
        }

        if (registration.Policy.PublishAccess == SharedChannelAccessMode.OwnerOnly)
        {
            return TopicPublishOutcome.SharedChannelAccessDenied;
        }

        CapabilityPolicyEvaluation evaluation = _grantRepository.Evaluate(
            publisher.AppId,
            publisher.UserId,
            registration.Policy.PublishCapability!,
            actingAuthority: AppAuthority.User,
            requiredAuthority: AppAuthority.User,
            registration.Policy.ResourceScope);

        return evaluation.Granted ? null : TopicPublishOutcome.SharedChannelAccessDenied;
    }

    private void EnsureSubscribeAllowed(string topicValue, PublisherIdentity subscriber)
    {
        SharedChannelRegistration? registration = FindGoverningSharedChannel(topicValue);
        if (registration is null
            || registration.Policy.SubscribeAccess == SharedChannelAccessMode.Open
            || string.Equals(registration.OwnerAppId, subscriber.AppId, StringComparison.Ordinal))
        {
            return;
        }

        if (registration.Policy.SubscribeAccess == SharedChannelAccessMode.OwnerOnly)
        {
            // CapabilityPolicyEvaluation.DenyMissing requires a positive revision; a repository that has
            // never issued a grant reports revision 0, mirroring the same clamp
            // FileSystemSelectedResourceHandleRegistry.Issue applies for the identical reason.
            throw new AppGatewayAccessDeniedException(
                $"shared-channel-owner-only:{topicValue}",
                CapabilityPolicyEvaluation.DenyMissing(Math.Max(_grantRepository.CurrentPolicyRevision, 1)));
        }

        CapabilityPolicyEvaluation evaluation = _grantRepository.Evaluate(
            subscriber.AppId,
            subscriber.UserId,
            registration.Policy.SubscribeCapability!,
            actingAuthority: AppAuthority.User,
            requiredAuthority: AppAuthority.User,
            registration.Policy.ResourceScope);

        if (!evaluation.Granted)
        {
            throw new AppGatewayAccessDeniedException(registration.Policy.SubscribeCapability!, evaluation);
        }
    }

    /// <summary>Finds the most specific registered shared channel whose root contains <paramref name="topicValue"/>, if any.</summary>
    private SharedChannelRegistration? FindGoverningSharedChannel(string topicValue)
    {
        lock (_gate)
        {
            SharedChannelRegistration? best = null;
            int bestRootLength = -1;
            foreach ((string root, SharedChannelRegistration registration) in _sharedChannelsByRoot)
            {
                bool matches = topicValue == root || topicValue.StartsWith(root + "/", StringComparison.Ordinal);
                if (matches && root.Length > bestRootLength)
                {
                    best = registration;
                    bestRootLength = root.Length;
                }
            }

            return best;
        }
    }

    private void Unsubscribe(string topicValue, Subscription subscription)
    {
        lock (_gate)
        {
            if (_subscriptionsByTopic.TryGetValue(topicValue, out List<Subscription>? subscriptions))
            {
                subscriptions.Remove(subscription);
            }
        }
    }

    private sealed class SharedChannelRegistration(SharedChannelPolicy policy, string ownerAppId)
    {
        public SharedChannelPolicy Policy { get; } = policy;
        public string OwnerAppId { get; } = ownerAppId;
    }

    /// <summary>
    /// One subscription: a local one has a CLR payload type and a typed handler; a wire one (a process
    /// in a worker, ADR 0124) has only the payload's wire name and receives a <see cref="WireTopicDelivery"/>.
    /// </summary>
    private sealed class Subscription
    {
        private Subscription(
            Type? payloadType,
            string wireName,
            Action<object>? invoke,
            Func<TopicName, object, string, DateTimeOffset, object>? createMessage,
            Action<WireTopicDelivery>? wireInvoke)
        {
            PayloadType = payloadType;
            WireName = wireName;
            Invoke = invoke;
            CreateMessage = createMessage;
            WireInvoke = wireInvoke;
        }

        /// <summary>Gets the CLR payload type of a local subscription; <see langword="null"/> for a wire one.</summary>
        public Type? PayloadType { get; }

        /// <summary>Gets the payload type's wire name (both kinds), matched by <see cref="IWireTopicMessageBus.PublishWire"/>.</summary>
        public string WireName { get; }

        /// <summary>Gets the typed handler of a local subscription, taking a <see cref="TopicMessage{TPayload}"/>.</summary>
        public Action<object>? Invoke { get; }

        /// <summary>Gets the factory of the local subscription's <see cref="TopicMessage{TPayload}"/> from an untyped payload.</summary>
        public Func<TopicName, object, string, DateTimeOffset, object>? CreateMessage { get; }

        /// <summary>Gets the handler of a wire subscription.</summary>
        public Action<WireTopicDelivery>? WireInvoke { get; }

        public static Subscription Local<TPayload>(Action<TopicMessage<TPayload>> handler) where TPayload : notnull => new(
            typeof(TPayload),
            WirePayloadNames.Of(typeof(TPayload)),
            message => handler((TopicMessage<TPayload>)message),
            static (topic, payload, publisherAppId, publishedAt) => new TopicMessage<TPayload>(topic, (TPayload)payload, publisherAppId, publishedAt),
            wireInvoke: null);

        public static Subscription Wire(string wireName, Action<WireTopicDelivery> handler) =>
            new(payloadType: null, wireName, invoke: null, createMessage: null, handler);
    }

    private sealed class Unsubscriber(InMemoryTopicMessageBus bus, string topicValue, Subscription subscription) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            bus.Unsubscribe(topicValue, subscription);
        }
    }

    private sealed class ChannelSubscription<TPayload>(IDisposable subscription, Channel<TopicMessage<TPayload>> channel)
        : ITopicChannelSubscription<TPayload> where TPayload : notnull
    {
        public ChannelReader<TopicMessage<TPayload>> Reader => channel.Reader;

        public ValueTask DisposeAsync()
        {
            subscription.Dispose();
            channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
