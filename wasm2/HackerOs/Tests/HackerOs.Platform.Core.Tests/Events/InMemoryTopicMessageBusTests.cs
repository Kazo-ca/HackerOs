using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.Platform.Core.Events;
using HackerOs.Platform.Core.Policy;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.Platform.Core.Tests.Events;

/// <summary>
/// Authorization and delivery tests for <c>MSG-001</c>/<c>MSG-005</c> —
/// <see cref="InMemoryTopicMessageBus"/>, per doc/wasm/dev/adr/0038-emitter-authorized-topic-messaging.md.
/// </summary>
public sealed class InMemoryTopicMessageBusTests
{
    private const string OwnerAppId = "org.hackeros.owner-app";
    private const string OtherAppId = "org.hackeros.other-app";
    private const string SharedCapability = AppCapabilities.NotificationsPost;

    private static PublisherIdentity Identity(string appId, string userId = "user-1", string processId = "1") =>
        new(appId, userId, processId);

    [Fact]
    public void Publish_within_own_namespace_delivers_to_subscribers()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        TopicName topic = TopicNames.ForApp(OwnerAppId).Segment("ticked").Build();
        TopicMessage<string>? received = null;

        using IDisposable sub = bus.Subscribe<string>(topic, Identity(OtherAppId), message => received = message);
        TopicPublishResult result = bus.Publish(topic, "hello", Identity(OwnerAppId));

        Assert.Equal(TopicPublishOutcome.Delivered, result.Outcome);
        Assert.Empty(result.SubscriberFaults);
        Assert.NotNull(received);
        Assert.Equal("hello", received!.Payload);
        Assert.Equal(OwnerAppId, received.PublisherAppId);
    }

    [Fact]
    public void Publish_outside_own_namespace_and_not_shared_is_denied()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        TopicName topic = TopicNames.ForApp(OwnerAppId).Segment("ticked").Build();
        bool received = false;

        using IDisposable sub = bus.Subscribe<string>(topic, Identity(OwnerAppId), _ => received = true);
        TopicPublishResult result = bus.Publish(topic, "hello", Identity(OtherAppId));

        Assert.Equal(TopicPublishOutcome.TopicNotOwnedByCaller, result.Outcome);
        Assert.False(received);
    }

    [Fact]
    public void SharedChannel_owner_may_always_publish_regardless_of_capability()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        TopicName root = TopicNames.Shared("test-channel").Segment("root").Build();
        bus.RegisterSharedChannel(
            root,
            new SharedChannelPolicy(SharedChannelAccessMode.RequiresCapability, SharedChannelAccessMode.Open, publishCapability: SharedCapability),
            Identity(OwnerAppId));
        bool received = false;

        using IDisposable sub = bus.Subscribe<string>(root, Identity(OwnerAppId), _ => received = true);
        TopicPublishResult result = bus.Publish(root, "hello", Identity(OwnerAppId));

        Assert.Equal(TopicPublishOutcome.Delivered, result.Outcome);
        Assert.True(received);
    }

    [Fact]
    public void SharedChannel_nonOwner_without_capability_is_denied()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        TopicName root = TopicNames.Shared("test-channel").Segment("root").Build();
        bus.RegisterSharedChannel(
            root,
            new SharedChannelPolicy(SharedChannelAccessMode.RequiresCapability, SharedChannelAccessMode.Open, publishCapability: SharedCapability),
            Identity(OwnerAppId));

        TopicPublishResult result = bus.Publish(root, "hello", Identity(OtherAppId));

        Assert.Equal(TopicPublishOutcome.SharedChannelAccessDenied, result.Outcome);
    }

    [Fact]
    public void SharedChannel_nonOwner_with_granted_capability_succeeds()
    {
        CapabilityGrantRepository grants = new();
        grants.Grant(OtherAppId, "user-1", SharedCapability, CapabilityGrantSource.UserApproval, AppAuthority.Administrator);
        InMemoryTopicMessageBus bus = new(grants);
        TopicName root = TopicNames.Shared("test-channel").Segment("root").Build();
        bus.RegisterSharedChannel(
            root,
            new SharedChannelPolicy(SharedChannelAccessMode.RequiresCapability, SharedChannelAccessMode.Open, publishCapability: SharedCapability),
            Identity(OwnerAppId));
        bool received = false;

        using IDisposable sub = bus.Subscribe<string>(root, Identity(OwnerAppId), _ => received = true);
        TopicPublishResult result = bus.Publish(root, "hello", Identity(OtherAppId));

        Assert.Equal(TopicPublishOutcome.Delivered, result.Outcome);
        Assert.True(received);
    }

    [Fact]
    public void SharedChannel_OwnerOnly_denies_a_nonOwner_publisher()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        TopicName root = TopicNames.Shared("test-channel").Segment("root").Build();
        bus.RegisterSharedChannel(root, SharedChannelPolicy.OwnerOnly(), Identity(OwnerAppId));

        TopicPublishResult result = bus.Publish(root, "hello", Identity(OtherAppId));

        Assert.Equal(TopicPublishOutcome.SharedChannelAccessDenied, result.Outcome);
    }

    [Fact]
    public void SharedChannel_Open_lets_any_app_publish_with_no_permission_at_all()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        TopicName root = TopicNames.Shared("test-channel").Segment("root").Build();
        bus.RegisterSharedChannel(root, SharedChannelPolicy.Open(), Identity(OwnerAppId));
        bool received = false;

        using IDisposable sub = bus.Subscribe<string>(root, Identity(OwnerAppId), _ => received = true);
        TopicPublishResult result = bus.Publish(root, "hello", Identity(OtherAppId));

        Assert.Equal(TopicPublishOutcome.Delivered, result.Outcome);
        Assert.True(received);
    }

    [Fact]
    public void RegisterSharedChannel_by_a_different_owner_throws()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        TopicName root = TopicNames.Shared("test-channel").Segment("root").Build();
        bus.RegisterSharedChannel(root, SharedChannelPolicy.OwnerOnly(), Identity(OwnerAppId));

        Assert.Throws<InvalidOperationException>(() =>
            bus.RegisterSharedChannel(root, SharedChannelPolicy.OwnerOnly(), Identity(OtherAppId)));
    }

    [Fact]
    public void RegisterSharedChannel_by_the_same_owner_is_idempotent()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        TopicName root = TopicNames.Shared("test-channel").Segment("root").Build();
        bus.RegisterSharedChannel(
            root,
            new SharedChannelPolicy(SharedChannelAccessMode.RequiresCapability, SharedChannelAccessMode.Open, publishCapability: SharedCapability),
            Identity(OwnerAppId));

        Exception? exception = Record.Exception(() =>
            bus.RegisterSharedChannel(root, SharedChannelPolicy.OwnerOnly(), Identity(OwnerAppId)));

        Assert.Null(exception);
    }

    [Fact]
    public void Subscribe_denied_when_shared_channel_requires_a_capability_the_subscriber_lacks()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        TopicName root = TopicNames.Shared("test-channel").Segment("root").Build();
        bus.RegisterSharedChannel(
            root,
            new SharedChannelPolicy(SharedChannelAccessMode.OwnerOnly, SharedChannelAccessMode.RequiresCapability, subscribeCapability: SharedCapability),
            Identity(OwnerAppId));

        AppGatewayAccessDeniedException exception = Assert.Throws<AppGatewayAccessDeniedException>(() =>
            bus.Subscribe<string>(root, Identity(OtherAppId), _ => { }));

        Assert.Equal(SharedCapability, exception.Capability);
        Assert.False(exception.Evaluation.Granted);
    }

    [Fact]
    public void Subscribe_denied_when_shared_channel_is_owner_only()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        TopicName root = TopicNames.Shared("test-channel").Segment("root").Build();
        bus.RegisterSharedChannel(root, SharedChannelPolicy.OwnerOnly(), Identity(OwnerAppId));

        Assert.Throws<AppGatewayAccessDeniedException>(() =>
            bus.Subscribe<string>(root, Identity(OtherAppId), _ => { }));
    }

    [Fact]
    public void Subscribe_allowed_when_shared_channel_capability_is_granted()
    {
        CapabilityGrantRepository grants = new();
        grants.Grant(OtherAppId, "user-1", SharedCapability, CapabilityGrantSource.UserApproval, AppAuthority.Administrator);
        InMemoryTopicMessageBus bus = new(grants);
        TopicName root = TopicNames.Shared("test-channel").Segment("root").Build();
        bus.RegisterSharedChannel(
            root,
            new SharedChannelPolicy(SharedChannelAccessMode.OwnerOnly, SharedChannelAccessMode.RequiresCapability, subscribeCapability: SharedCapability),
            Identity(OwnerAppId));

        using IDisposable sub = bus.Subscribe<string>(root, Identity(OtherAppId), _ => { });

        Assert.NotNull(sub);
    }

    [Fact]
    public void Subscribe_to_an_app_owned_topic_from_another_app_is_unrestricted()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        TopicName topic = TopicNames.ForApp(OwnerAppId).Segment("ticked").Build();
        bool received = false;

        using IDisposable sub = bus.Subscribe<string>(topic, Identity(OtherAppId), _ => received = true);
        bus.Publish(topic, "hello", Identity(OwnerAppId));

        Assert.True(received);
    }

    [Fact]
    public void A_faulting_plain_subscriber_is_isolated_and_reported_as_a_fault()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        TopicName topic = TopicNames.ForApp(OwnerAppId).Segment("ticked").Build();
        bool laterRan = false;

        using IDisposable faulting = bus.Subscribe<string>(topic, Identity(OwnerAppId), _ => throw new InvalidOperationException("boom"));
        using IDisposable later = bus.Subscribe<string>(topic, Identity(OwnerAppId), _ => laterRan = true);

        TopicPublishResult result = bus.Publish(topic, "hello", Identity(OwnerAppId));

        Assert.True(laterRan);
        Assert.Equal(TopicPublishOutcome.Delivered, result.Outcome);
        Assert.Single(result.SubscriberFaults);
        Assert.Equal(typeof(string), result.SubscriberFaults[0].EventType);
        Assert.IsType<InvalidOperationException>(result.SubscriberFaults[0].Exception);
    }

    [Fact]
    public async Task A_faulting_plain_subscriber_does_not_prevent_a_sibling_channel_subscription_from_receiving()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        TopicName topic = TopicNames.ForApp(OwnerAppId).Segment("ticked").Build();

        using IDisposable faulting = bus.Subscribe<string>(topic, Identity(OwnerAppId), _ => throw new InvalidOperationException("boom"));
        await using ITopicChannelSubscription<string> channelSub = bus.SubscribeChannel<string>(topic, Identity(OwnerAppId));

        TopicPublishResult result = bus.Publish(topic, "hello", Identity(OwnerAppId));

        Assert.Single(result.SubscriberFaults);
        bool gotMessage = channelSub.Reader.TryRead(out TopicMessage<string>? message);
        Assert.True(gotMessage);
        Assert.Equal("hello", message!.Payload);
    }

    [Fact]
    public async Task Disposing_a_channel_subscription_completes_the_channel_and_stops_delivery()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        TopicName topic = TopicNames.ForApp(OwnerAppId).Segment("ticked").Build();

        ITopicChannelSubscription<string> channelSub = bus.SubscribeChannel<string>(topic, Identity(OwnerAppId));
        bus.Publish(topic, "first", Identity(OwnerAppId));
        Assert.True(channelSub.Reader.TryRead(out _));

        await channelSub.DisposeAsync();
        bus.Publish(topic, "second", Identity(OwnerAppId));

        Assert.False(channelSub.Reader.TryRead(out _));
        Assert.True(channelSub.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void SharedChannelPolicy_RequiresCapability_without_a_capability_throws()
    {
        // ThrowsAny: an omitted (null) capability throws ArgumentNullException, a derived type of ArgumentException.
        Assert.ThrowsAny<ArgumentException>(() =>
            new SharedChannelPolicy(SharedChannelAccessMode.RequiresCapability, SharedChannelAccessMode.Open));
    }

    [Fact]
    public void SharedChannelPolicy_Open_with_a_capability_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new SharedChannelPolicy(SharedChannelAccessMode.Open, SharedChannelAccessMode.Open, publishCapability: SharedCapability));
    }

    [Fact]
    public void Topic_matching_for_delivery_is_exact_not_prefix()
    {
        InMemoryTopicMessageBus bus = new(new CapabilityGrantRepository());
        TopicName parent = TopicNames.ForApp(OwnerAppId).Segment("changed").Build();
        TopicName child = TopicNames.ForApp(OwnerAppId).Segment("changed").Segment("home-alice").Build();
        bool parentReceived = false;

        using IDisposable sub = bus.Subscribe<string>(parent, Identity(OwnerAppId), _ => parentReceived = true);
        bus.Publish(child, "hello", Identity(OwnerAppId));

        Assert.False(parentReceived);
    }
}
