using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace HackerOs.Simulation.Abstractions.Events;

/// <summary>
/// Identifies one validated, namespaced messaging topic. Instances are only ever produced through
/// <see cref="TopicNames"/>/<see cref="TopicNameBuilder"/> — never constructed from a hand-typed string —
/// so a publisher and a subscriber can only ever agree on a topic by sharing the same helper call. See
/// <c>doc/wasm/dev/old/Global-FileView-And-MessagingSystem/MessagingSystem.md</c> (<c>MSG-002</c>) and
/// <c>doc/wasm/dev/adr/0038-emitter-authorized-topic-messaging.md</c>.
/// </summary>
public readonly record struct TopicName
{
    internal TopicName(string value)
    {
        Value = value;
    }

    /// <summary>Gets the canonical, slash-separated topic path.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Builds one <see cref="TopicName"/> from validated segments.</summary>
public sealed partial class TopicNameBuilder
{
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();

    private readonly string _root;
    private readonly List<string> _segments = [];

    internal TopicNameBuilder(string root)
    {
        _root = root;
    }

    /// <summary>Appends one validated lowercase-kebab-case segment (no <c>/</c>, whitespace, or wildcard characters).</summary>
    /// <exception cref="ArgumentException"><paramref name="segment"/> is empty or not lowercase kebab-case.</exception>
    public TopicNameBuilder Segment(string segment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segment);
        if (!SegmentPattern().IsMatch(segment))
        {
            throw new ArgumentException(
                $"Topic segments must be lowercase kebab-case ('{segment}' is invalid).", nameof(segment));
        }

        _segments.Add(segment);
        return this;
    }

    /// <summary>Produces the immutable, validated topic name.</summary>
    /// <exception cref="InvalidOperationException">No segment was appended.</exception>
    public TopicName Build()
    {
        if (_segments.Count == 0)
        {
            throw new InvalidOperationException("A topic requires at least one segment.");
        }

        return new TopicName($"{_root}/{string.Join('/', _segments)}");
    }
}

/// <summary>
/// Entry point for building topic names. An app-owned topic (<see cref="ForApp"/>) is only publishable by
/// that app; a shared topic (<see cref="Shared"/>) requires the owner to call
/// <see cref="ITopicMessageBus.RegisterSharedChannel"/> before anyone can publish to it.
/// </summary>
public static class TopicNames
{
    /// <summary>Root topic path segment every app-owned topic falls under.</summary>
    internal const string AppNamespaceRoot = "app";

    /// <summary>Root topic path segment every shared-channel topic falls under.</summary>
    internal const string SharedNamespaceRoot = "shared";

    /// <summary>Starts building a topic owned by one app's own reverse-domain namespace.</summary>
    /// <exception cref="ArgumentException"><paramref name="appId"/> is empty, or contains <c>/</c> or whitespace.</exception>
    public static TopicNameBuilder ForApp(string appId) =>
        new($"{AppNamespaceRoot}/{ValidateRootComponent(appId, nameof(appId))}");

    /// <summary>Starts building a topic under a platform-owned shared root (e.g. <c>"filesystem"</c>).</summary>
    /// <exception cref="ArgumentException"><paramref name="sharedRootName"/> is empty, or contains <c>/</c> or whitespace.</exception>
    public static TopicNameBuilder Shared(string sharedRootName) =>
        new($"{SharedNamespaceRoot}/{ValidateRootComponent(sharedRootName, nameof(sharedRootName))}");

    /// <summary>
    /// Gets whether <paramref name="topic"/> falls under <paramref name="appId"/>'s own namespace. Used
    /// by <see cref="ITopicMessageBus"/> implementations to evaluate publish authorization.
    /// </summary>
    public static bool IsOwnedByApp(string topic, string appId)
    {
        string prefix = $"{AppNamespaceRoot}/{appId}";
        return topic == prefix || topic.StartsWith(prefix + "/", StringComparison.Ordinal);
    }

    private static string ValidateRootComponent(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Contains('/') || value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Topic root components cannot contain '/' or whitespace.", parameterName);
        }

        return value;
    }
}

/// <summary>
/// Builds well-formed <see cref="App.Abstractions.TopicPermissions"/> capability identifiers from an
/// already-validated <see cref="TopicName"/>, so a declared or requested permission is never a hand-typed
/// string. See <c>doc/wasm/dev/adr/0040-declared-topic-permissions.md</c>.
/// </summary>
public static class TopicPermissionNames
{
    /// <summary>Gets the publish-side permission identifier gating <paramref name="root"/>.</summary>
    public static string ToPublishPermission(this TopicName root) => App.Abstractions.TopicPermissions.PublishPrefix + root.Value;

    /// <summary>Gets the subscribe-side permission identifier gating <paramref name="root"/>.</summary>
    public static string ToSubscribePermission(this TopicName root) => App.Abstractions.TopicPermissions.SubscribePrefix + root.Value;
}

/// <summary>Envelope for one delivered topic message.</summary>
/// <param name="Topic">Topic the message was published on.</param>
/// <param name="Payload">Typed message payload.</param>
/// <param name="PublisherAppId">App ID of the trusted publisher, stamped by the bus, never by the caller.</param>
/// <param name="PublishedAtUtc">Publication time.</param>
public sealed record TopicMessage<TPayload>(
    TopicName Topic,
    TPayload Payload,
    string PublisherAppId,
    DateTimeOffset PublishedAtUtc) where TPayload : notnull;

/// <summary>Identifies the stable outcome of one <see cref="ITopicMessageBus.Publish{TPayload}"/> call.</summary>
public enum TopicPublishOutcome
{
    /// <summary>The message was delivered to every current subscriber.</summary>
    Delivered,

    /// <summary>The topic is not owned by the publisher and is not a shared channel the publisher may use.</summary>
    TopicNotOwnedByCaller,

    /// <summary>The topic is a shared channel, but policy denied the publisher's capability.</summary>
    SharedChannelAccessDenied
}

/// <summary>Contains the result of one publish attempt.</summary>
/// <param name="Outcome">Stable publish outcome.</param>
/// <param name="SubscriberFaults">Every fault raised by a subscriber while handling this message, in delivery order.</param>
public sealed record TopicPublishResult(
    TopicPublishOutcome Outcome,
    IReadOnlyList<EventDispatchFault> SubscriberFaults);

/// <summary>
/// Trusted caller identity for a publish or subscribe call, supplied only by the execution-context/
/// gateway factory — never self-declared by app code — mirroring how <c>AppOperationContext</c> is
/// trusted elsewhere in this codebase.
/// </summary>
/// <param name="AppId">Acting app's identifier.</param>
/// <param name="UserId">Acting user identifier.</param>
/// <param name="ProcessId">Acting process identifier, opaque string form.</param>
public readonly record struct PublisherIdentity(string AppId, string UserId, string ProcessId);

/// <summary>
/// Identifies how a shared channel restricts one direction (publish or subscribe) of access. A
/// permission is entirely optional per channel: the owner decides, per direction, whether to require
/// one at all.
/// </summary>
public enum SharedChannelAccessMode
{
    /// <summary>No restriction: any app may act, with no permission or ownership check — an ad hoc,
    /// "SendMessage"-style channel where the owner deliberately declares no permission is needed.</summary>
    Open,

    /// <summary>Only the channel's registered owner may act.</summary>
    OwnerOnly,

    /// <summary>The acting app must hold the paired capability; the channel owner always bypasses this check.</summary>
    RequiresCapability
}

/// <summary>
/// Declares one shared channel's access policy, independently for publish and subscribe. Reuses the
/// existing deny-by-default capability model (<c>HackerOs.App.Abstractions.Policy</c>) for the
/// <see cref="SharedChannelAccessMode.RequiresCapability"/> tier instead of introducing a new
/// authorization mechanism; grants for a capability declared this way go through the same
/// explicit-approval flow (<c>CapabilityGrantSource.UserApproval</c>/<c>AdministratorApproval</c>) as
/// every other capability, per <c>doc/wasm/dev/adr/0038-emitter-authorized-topic-messaging.md</c>.
/// </summary>
public sealed record SharedChannelPolicy
{
    /// <summary>Initializes a validated per-direction access policy.</summary>
    /// <param name="publishAccess">Access mode governing publish.</param>
    /// <param name="subscribeAccess">Access mode governing subscribe.</param>
    /// <param name="publishCapability">
    /// Required exactly when <paramref name="publishAccess"/> is <see cref="SharedChannelAccessMode.RequiresCapability"/>;
    /// forbidden otherwise.
    /// </param>
    /// <param name="subscribeCapability">
    /// Required exactly when <paramref name="subscribeAccess"/> is <see cref="SharedChannelAccessMode.RequiresCapability"/>;
    /// forbidden otherwise.
    /// </param>
    /// <param name="resourceScope">Optional structured resource constraint reused from the capability grant model.</param>
    public SharedChannelPolicy(
        SharedChannelAccessMode publishAccess,
        SharedChannelAccessMode subscribeAccess,
        string? publishCapability = null,
        string? subscribeCapability = null,
        App.Abstractions.Policy.CapabilityResourceCandidate? resourceScope = null)
    {
        ValidateAccess(publishAccess, nameof(publishAccess), publishCapability, nameof(publishCapability));
        ValidateAccess(subscribeAccess, nameof(subscribeAccess), subscribeCapability, nameof(subscribeCapability));

        PublishAccess = publishAccess;
        SubscribeAccess = subscribeAccess;
        PublishCapability = publishCapability;
        SubscribeCapability = subscribeCapability;
        ResourceScope = resourceScope;
    }

    /// <summary>Gets the access mode governing publish.</summary>
    public SharedChannelAccessMode PublishAccess { get; }

    /// <summary>Gets the access mode governing subscribe.</summary>
    public SharedChannelAccessMode SubscribeAccess { get; }

    /// <summary>Gets the capability required to publish, set only when <see cref="PublishAccess"/> is <see cref="SharedChannelAccessMode.RequiresCapability"/>.</summary>
    public string? PublishCapability { get; }

    /// <summary>Gets the capability required to subscribe, set only when <see cref="SubscribeAccess"/> is <see cref="SharedChannelAccessMode.RequiresCapability"/>.</summary>
    public string? SubscribeCapability { get; }

    /// <summary>Gets the optional structured resource constraint reused from the capability grant model.</summary>
    public App.Abstractions.Policy.CapabilityResourceCandidate? ResourceScope { get; }

    /// <summary>A channel fully open in both directions — no permission at all, matching an ad hoc "SendMessage"-style channel.</summary>
    public static SharedChannelPolicy Open() => new(SharedChannelAccessMode.Open, SharedChannelAccessMode.Open);

    /// <summary>A channel only its owner may publish to or subscribe on.</summary>
    public static SharedChannelPolicy OwnerOnly() => new(SharedChannelAccessMode.OwnerOnly, SharedChannelAccessMode.OwnerOnly);

    private static void ValidateAccess(
        SharedChannelAccessMode access, string accessParameterName, string? capability, string capabilityParameterName)
    {
        if (!Enum.IsDefined(access))
        {
            throw new ArgumentOutOfRangeException(accessParameterName, access, "Unknown shared-channel access mode.");
        }

        if (access == SharedChannelAccessMode.RequiresCapability)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(capability, capabilityParameterName);
        }
        else if (capability is not null)
        {
            throw new ArgumentException(
                $"'{capabilityParameterName}' may only be set when the matching access mode is " +
                $"{nameof(SharedChannelAccessMode.RequiresCapability)}.",
                capabilityParameterName);
        }
    }
}

/// <summary>
/// A disposable, <see cref="Channel{T}"/>-backed subscription: disposing stops delivery and completes
/// the channel, so a consumer can simply <c>await foreach</c> the <see cref="Reader"/> and dispose to
/// unsubscribe.
/// </summary>
public interface ITopicChannelSubscription<TPayload> : IAsyncDisposable where TPayload : notnull
{
    /// <summary>Gets the channel reader messages are delivered on.</summary>
    ChannelReader<TopicMessage<TPayload>> Reader { get; }
}

/// <summary>
/// Publishes and subscribes to named, namespaced topics with emitter authorization, extending (not
/// replacing) the existing <see cref="IEventBus"/> kernel-event lane. See
/// <c>doc/wasm/dev/old/Global-FileView-And-MessagingSystem/MessagingSystem.md</c> and
/// <c>doc/wasm/dev/adr/0038-emitter-authorized-topic-messaging.md</c> for the full specification.
/// Implemented by <c>HackerOs.Platform.Core.Events.InMemoryTopicMessageBus</c> (<c>MSG-001</c>).
/// </summary>
public interface ITopicMessageBus
{
    /// <summary>
    /// Subscribes <paramref name="subscriber"/> to every message published on <paramref name="topic"/>.
    /// </summary>
    /// <exception cref="Gateways.AppGatewayAccessDeniedException">
    /// <paramref name="topic"/> falls under a shared channel whose policy denies <paramref name="subscriber"/>.
    /// </exception>
    IDisposable Subscribe<TPayload>(TopicName topic, PublisherIdentity subscriber, Action<TopicMessage<TPayload>> handler)
        where TPayload : notnull;

    /// <summary>
    /// Returns a disposable, <see cref="Channel{T}"/>-backed subscription for <paramref name="topic"/>.
    /// </summary>
    /// <param name="topic">Topic to subscribe to.</param>
    /// <param name="subscriber">Trusted subscriber identity.</param>
    /// <param name="boundedCapacity">Optional bounded channel capacity; unbounded when <see langword="null"/>.</param>
    /// <exception cref="Gateways.AppGatewayAccessDeniedException">
    /// <paramref name="topic"/> falls under a shared channel whose policy denies <paramref name="subscriber"/>.
    /// </exception>
    ITopicChannelSubscription<TPayload> SubscribeChannel<TPayload>(
        TopicName topic, PublisherIdentity subscriber, int? boundedCapacity = null)
        where TPayload : notnull;

    /// <summary>Publishes one message, enforcing namespace ownership or shared-channel policy for <paramref name="publisher"/>.</summary>
    TopicPublishResult Publish<TPayload>(TopicName topic, TPayload payload, PublisherIdentity publisher)
        where TPayload : notnull;

    /// <summary>
    /// Idempotently registers <paramref name="root"/> as a shared channel owned by <paramref name="owner"/>.
    /// A later call for the same <paramref name="root"/> by the same owner replaces the stored policy.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="root"/> is already registered by a different owner.</exception>
    void RegisterSharedChannel(TopicName root, SharedChannelPolicy policy, PublisherIdentity owner);
}
