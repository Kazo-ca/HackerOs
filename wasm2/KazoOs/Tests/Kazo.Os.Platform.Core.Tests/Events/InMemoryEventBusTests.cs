using Kazo.Os.Platform.Core.Events;

namespace Kazo.Os.Platform.Core.Tests.Events;

public sealed class InMemoryEventBusTests
{
    private sealed record SampleEvent(string Message);

    private sealed record OtherEvent;

    [Fact]
    public void Subscribers_are_invoked_in_subscription_order()
    {
        InMemoryEventBus bus = new();
        List<string> order = [];

        using IDisposable first = bus.Subscribe<SampleEvent>(e => order.Add($"first:{e.Message}"));
        using IDisposable second = bus.Subscribe<SampleEvent>(e => order.Add($"second:{e.Message}"));

        bus.Publish(new SampleEvent("hello"));

        Assert.Equal(["first:hello", "second:hello"], order);
    }

    [Fact]
    public void Disposing_a_subscription_stops_further_delivery()
    {
        InMemoryEventBus bus = new();
        int callCount = 0;

        IDisposable subscription = bus.Subscribe<SampleEvent>(_ => callCount++);
        bus.Publish(new SampleEvent("first"));
        subscription.Dispose();
        bus.Publish(new SampleEvent("second"));

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void A_faulting_subscriber_is_isolated_and_does_not_block_later_subscribers()
    {
        InMemoryEventBus bus = new();
        bool laterRan = false;

        using IDisposable faulting = bus.Subscribe<SampleEvent>(_ => throw new InvalidOperationException("boom"));
        using IDisposable later = bus.Subscribe<SampleEvent>(_ => laterRan = true);

        IReadOnlyList<Simulation.Abstractions.Events.EventDispatchFault> faults = bus.Publish(new SampleEvent("x"));

        Assert.True(laterRan);
        Assert.Single(faults);
        Assert.Equal(typeof(SampleEvent), faults[0].EventType);
        Assert.IsType<InvalidOperationException>(faults[0].Exception);
    }

    [Fact]
    public void Publishing_an_event_with_no_subscribers_returns_no_faults_and_does_not_throw()
    {
        InMemoryEventBus bus = new();

        IReadOnlyList<Simulation.Abstractions.Events.EventDispatchFault> faults = bus.Publish(new OtherEvent());

        Assert.Empty(faults);
    }

    [Fact]
    public void Subscribers_of_a_different_event_type_are_not_invoked()
    {
        InMemoryEventBus bus = new();
        bool sampleHandlerRan = false;

        using IDisposable subscription = bus.Subscribe<SampleEvent>(_ => sampleHandlerRan = true);
        bus.Publish(new OtherEvent());

        Assert.False(sampleHandlerRan);
    }
}
