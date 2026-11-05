using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.Tests.Events;

/// <summary>
/// ADR 0124, section 1: the buses serve processes in Web Workers by wire name — same authorization as
/// the typed members, an object for a subscriber on the main thread, JSON passed through for another worker.
/// </summary>
public sealed class WireTopicMessageBusTests
{
    private const string OwnerAppId = "org.kazoos.owner-app";
    private const string OtherAppId = "org.kazoos.other-app";

    private static PublisherIdentity Identity(string appId) => new(appId, "user-1", "1");

    [Fact]
    public void A_main_thread_publication_reaches_a_wire_subscriber_as_the_object_and_its_type()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        IWireTopicMessageBus wire = bus;
        TopicName topic = TopicNames.ForApp(OwnerAppId).Segment("ticked").Build();
        List<WireTopicDelivery> deliveries = [];

        using IDisposable subscription = wire.SubscribeWire(topic, Identity(OtherAppId), WirePayloadNames.Of(typeof(string)), deliveries.Add);
        bus.Publish(topic, "hello", Identity(OwnerAppId));
        bus.Publish(topic, 42, Identity(OwnerAppId)); // another payload type: not for this subscriber

        WireTopicDelivery delivery = Assert.Single(deliveries);
        Assert.Equal("hello", delivery.Payload);
        Assert.Equal(typeof(string), delivery.PayloadType);
        Assert.Null(delivery.Json);
        Assert.Equal(OwnerAppId, delivery.PublisherAppId);
    }

    [Fact]
    public void A_worker_publication_is_rebuilt_once_per_type_for_main_thread_subscribers_and_passed_as_json_to_workers()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        IWireTopicMessageBus wire = bus;
        TopicName topic = TopicNames.ForApp(OwnerAppId).Segment("ticked").Build();
        List<string> local = [];
        List<string?> remote = [];
        int materialized = 0;

        using IDisposable first = bus.Subscribe<string>(topic, Identity(OtherAppId), message => local.Add(message.Payload));
        using IDisposable second = bus.Subscribe<string>(topic, Identity(OtherAppId), message => local.Add(message.Payload + "!"));
        using IDisposable third = wire.SubscribeWire(topic, Identity(OtherAppId), WirePayloadNames.Of(typeof(string)), delivery => remote.Add(delivery.Json));
        TopicPublishResult result = wire.PublishWire(
            topic, WirePayloadNames.Of(typeof(string)), "\"hi\"", _ => { materialized++; return "hi"; }, Identity(OwnerAppId));

        Assert.Equal(TopicPublishOutcome.Delivered, result.Outcome);
        Assert.Equal(["hi", "hi!"], local);
        Assert.Equal(["\"hi\""], remote);
        Assert.Equal(1, materialized);
        Assert.Equal(3, wire.CountSubscriptions(topic));
    }

    [Fact]
    public void A_wire_publication_and_subscription_follow_the_same_rules_as_typed_ones()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        IWireTopicMessageBus wire = bus;
        TopicName foreign = TopicNames.ForApp(OwnerAppId).Segment("ticked").Build();
        TopicName locked = TopicNames.Shared("locked").Segment("data").Build();
        bus.RegisterSharedChannel(locked, SharedChannelPolicy.OwnerOnly(), Identity(OwnerAppId));

        TopicPublishResult refused = wire.PublishWire(foreign, "x", "{}", static _ => throw new InvalidOperationException(), Identity(OtherAppId));

        Assert.Equal(TopicPublishOutcome.TopicNotOwnedByCaller, refused.Outcome);
        Assert.Throws<AppGatewayAccessDeniedException>(() =>
            wire.SubscribeWire(locked, Identity(OtherAppId), "x", static _ => { }));
        Assert.Equal(0, wire.CountSubscriptions(locked));
    }

    [Fact]
    public void A_kernel_event_reaches_a_wire_subscriber_until_it_unsubscribes()
    {
        InMemoryEventBus bus = new();
        IWireEventBus wire = bus;
        string name = WirePayloadNames.Of(typeof(AppDisabledEvent));
        List<object> received = [];

        IDisposable subscription = wire.SubscribeWire(name, (type, @event) => received.Add(@event));
        bus.Publish(new AppDisabledEvent("org.kazoos.gone"));
        subscription.Dispose();
        bus.Publish(new AppDisabledEvent("org.kazoos.later"));

        Assert.Equal([new AppDisabledEvent("org.kazoos.gone")], received);
        Assert.Equal(0, wire.CountWireSubscriptions(name));
    }
}
