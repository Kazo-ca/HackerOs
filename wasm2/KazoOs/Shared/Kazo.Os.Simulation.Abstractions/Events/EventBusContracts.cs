namespace Kazo.Os.Simulation.Abstractions.Events;

/// <summary>
/// Publishes and subscribes to strongly typed in-process events between platform subsystems and
/// apps, without any UI, network, or persistence involvement.
/// </summary>
/// <remarks>
/// Subscriptions are strong references held until explicitly disposed. Callers (apps, services,
/// processes) MUST dispose their subscription when they stop, or the bus will keep invoking a
/// handler that outlives its owner. See ADR 0012/0013 lifecycle rules and P1-SYS-011 tests for the
/// unsubscribe-on-stop discipline this contract depends on.
/// </remarks>
public interface IEventBus
{
    /// <summary>
    /// Subscribes to every event of type <typeparamref name="TEvent"/>, invoked in the order
    /// subscriptions were added.
    /// </summary>
    /// <typeparam name="TEvent">Concrete event type; events are matched by exact type, not by
    /// inheritance.</typeparam>
    /// <param name="handler">Callback invoked synchronously for each published event.</param>
    /// <returns>A handle that removes the subscription when disposed.</returns>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull;

    /// <summary>
    /// Publishes one event synchronously to every current subscriber of
    /// <typeparamref name="TEvent"/>, in subscription order.
    /// </summary>
    /// <remarks>
    /// A handler exception is isolated: it does not prevent later handlers from running and does
    /// not propagate to the publisher. Every fault is returned so the caller/tests can observe it.
    /// </remarks>
    /// <typeparam name="TEvent">Concrete event type being published.</typeparam>
    /// <param name="event">The event instance to deliver.</param>
    /// <returns>Every fault raised by a subscriber while handling this event, in delivery order.</returns>
    IReadOnlyList<EventDispatchFault> Publish<TEvent>(TEvent @event) where TEvent : notnull;
}

/// <summary>Records one subscriber exception raised while dispatching one event.</summary>
/// <param name="EventType">Concrete type of the event being dispatched.</param>
/// <param name="Exception">Exception thrown by the faulting subscriber.</param>
public sealed record EventDispatchFault(Type EventType, Exception Exception);
