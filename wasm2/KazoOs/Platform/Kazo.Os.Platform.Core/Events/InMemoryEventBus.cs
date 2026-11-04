using Kazo.Os.Simulation.Abstractions.Events;

namespace Kazo.Os.Platform.Core.Events;

/// <summary>
/// In-memory <see cref="IEventBus"/> implementation used by every headless and Blazor host.
/// </summary>
public sealed class InMemoryEventBus : IEventBus, IWireEventBus
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Type, List<Subscription>> _subscriptionsByEventType = [];

    // ADR 0124: subscriptions of processes in Web Workers, by the event type's wire name.
    private readonly Dictionary<string, List<WireSubscription>> _wireSubscriptionsByName = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);

        Subscription subscription = new(@event => handler((TEvent)@event));

        lock (_gate)
        {
            if (!_subscriptionsByEventType.TryGetValue(typeof(TEvent), out List<Subscription>? subscriptions))
            {
                subscriptions = [];
                _subscriptionsByEventType[typeof(TEvent)] = subscriptions;
            }

            subscriptions.Add(subscription);
        }

        return new Unsubscriber(this, typeof(TEvent), subscription);
    }

    /// <inheritdoc />
    public IReadOnlyList<EventDispatchFault> Publish<TEvent>(TEvent @event) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(@event);

        List<Subscription> snapshot;
        List<WireSubscription>? wireSnapshot = null;
        lock (_gate)
        {
            snapshot = _subscriptionsByEventType.TryGetValue(typeof(TEvent), out List<Subscription>? subscriptions)
                ? [.. subscriptions]
                : [];
            if (_wireSubscriptionsByName.Count > 0
                && _wireSubscriptionsByName.TryGetValue(WirePayloadNames.Of(typeof(TEvent)), out List<WireSubscription>? wire))
            {
                wireSnapshot = [.. wire];
            }
        }

        if (snapshot.Count == 0 && wireSnapshot is null)
        {
            return [];
        }

        List<EventDispatchFault>? faults = null;
        foreach (Subscription subscription in snapshot)
        {
            try
            {
                subscription.Invoke(@event);
            }
            catch (Exception exception)
            {
                faults ??= [];
                faults.Add(new EventDispatchFault(typeof(TEvent), exception));
            }
        }

        // A subscriber in a worker only queues the event for its worker; its own handler runs later, in
        // its own runtime, so its faults never come back here. A failure to queue is reported like any.
        foreach (WireSubscription subscription in wireSnapshot ?? [])
        {
            try
            {
                subscription.Handler(typeof(TEvent), @event);
            }
            catch (Exception exception)
            {
                faults ??= [];
                faults.Add(new EventDispatchFault(typeof(TEvent), exception));
            }
        }

        return faults ?? (IReadOnlyList<EventDispatchFault>)[];
    }

    /// <inheritdoc />
    IDisposable IWireEventBus.SubscribeWire(string eventWireName, Action<Type, object> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventWireName);
        ArgumentNullException.ThrowIfNull(handler);
        WireSubscription subscription = new(handler);
        lock (_gate)
        {
            if (!_wireSubscriptionsByName.TryGetValue(eventWireName, out List<WireSubscription>? subscriptions))
            {
                subscriptions = [];
                _wireSubscriptionsByName[eventWireName] = subscriptions;
            }

            subscriptions.Add(subscription);
        }

        return new WireUnsubscriber(this, eventWireName, subscription);
    }

    /// <inheritdoc />
    int IWireEventBus.CountWireSubscriptions(string eventWireName)
    {
        lock (_gate)
        {
            return _wireSubscriptionsByName.TryGetValue(eventWireName, out List<WireSubscription>? subscriptions) ? subscriptions.Count : 0;
        }
    }

    private void UnsubscribeWire(string eventWireName, WireSubscription subscription)
    {
        lock (_gate)
        {
            if (_wireSubscriptionsByName.TryGetValue(eventWireName, out List<WireSubscription>? subscriptions)
                && subscriptions.Remove(subscription)
                && subscriptions.Count == 0)
            {
                _wireSubscriptionsByName.Remove(eventWireName);
            }
        }
    }

    private void Unsubscribe(Type eventType, Subscription subscription)
    {
        lock (_gate)
        {
            if (_subscriptionsByEventType.TryGetValue(eventType, out List<Subscription>? subscriptions))
            {
                subscriptions.Remove(subscription);
            }
        }
    }

    private sealed class Subscription(Action<object> invoke)
    {
        public void Invoke(object @event) => invoke(@event);
    }

    private sealed class WireSubscription(Action<Type, object> handler)
    {
        public Action<Type, object> Handler { get; } = handler;
    }

    private sealed class WireUnsubscriber(InMemoryEventBus bus, string eventWireName, WireSubscription subscription) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                bus.UnsubscribeWire(eventWireName, subscription);
            }
        }
    }

    private sealed class Unsubscriber(InMemoryEventBus bus, Type eventType, Subscription subscription) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            bus.Unsubscribe(eventType, subscription);
        }
    }
}
