using Kazo.Os.Simulation.Abstractions.Events;

namespace Kazo.Os.Platform.Core.Events;

/// <summary>
/// Names a payload type the same way in every .NET runtime of the OS (ADR 0124, section 2): a process
/// in a Web Worker subscribes by this name, because its runtime cannot hand a <see cref="Type"/> to the
/// OS's runtime. The name is the type's full name and its assembly's simple name, without version, so
/// the same app assembly loaded on the main thread and in a worker agrees on it.
/// </summary>
internal static class WirePayloadNames
{
    /// <summary>Gets the wire name of <paramref name="type"/>: <c>Namespace.Type, Assembly</c>.</summary>
    public static string Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return $"{type.FullName}, {type.Assembly.GetName().Name}";
    }
}

/// <summary>
/// One topic message delivered to a subscription made on behalf of a process in a worker (ADR 0124,
/// section 1). The payload is either the publisher's object (a publisher on the main thread: the
/// worker's side serializes it) or the JSON a publisher in a worker already wrote.
/// </summary>
/// <param name="Topic">Topic the message was published on.</param>
/// <param name="PublisherAppId">App ID of the trusted publisher, stamped by the bus.</param>
/// <param name="PublishedAtUtc">Publication time.</param>
/// <param name="PayloadType">CLR type of <paramref name="Payload"/>, when the publisher was on the main thread.</param>
/// <param name="Payload">The publisher's object, when it was on the main thread.</param>
/// <param name="Json">The serialized payload, when the publisher was in a worker.</param>
internal sealed record WireTopicDelivery(
    TopicName Topic,
    string PublisherAppId,
    DateTimeOffset PublishedAtUtc,
    Type? PayloadType,
    object? Payload,
    string? Json);

/// <summary>
/// The part of a topic bus that serves processes in Web Workers (ADR 0124, section 1): subscriptions
/// and publications keyed by the payload's wire name (<see cref="WirePayloadNames"/>) instead of a CLR
/// type the OS's runtime may not even have loaded. Authorization is exactly that of the typed members.
/// </summary>
internal interface IWireTopicMessageBus
{
    /// <summary>
    /// Subscribes, on behalf of <paramref name="subscriber"/>, to every message of <paramref name="topic"/>
    /// whose payload type has the wire name <paramref name="payloadWireName"/>.
    /// </summary>
    /// <exception cref="Kazo.Os.Simulation.Abstractions.Gateways.AppGatewayAccessDeniedException">
    /// The topic falls under a shared channel whose policy denies <paramref name="subscriber"/>.
    /// </exception>
    IDisposable SubscribeWire(TopicName topic, PublisherIdentity subscriber, string payloadWireName, Action<WireTopicDelivery> handler);

    /// <summary>
    /// Publishes a payload a process in a worker serialized. Subscribers on the main thread receive
    /// the object <paramref name="materialize"/> rebuilds for their payload type (once per type);
    /// subscribers in workers receive the JSON as is.
    /// </summary>
    TopicPublishResult PublishWire(
        TopicName topic, string payloadWireName, string json, Func<Type, object> materialize, PublisherIdentity publisher);

    /// <summary>Gets the number of subscriptions on <paramref name="topic"/> (diagnostics and tests).</summary>
    int CountSubscriptions(TopicName topic);
}

/// <summary>
/// The part of the kernel event bus that serves processes in Web Workers (ADR 0124, section 1): a
/// subscription by wire name. Apps never publish typed events (no type is app-publishable), so there
/// is no wire publication.
/// </summary>
internal interface IWireEventBus
{
    /// <summary>
    /// Subscribes to every event whose type has the wire name <paramref name="eventWireName"/>; the
    /// handler receives the event object and its type, which the worker's side serializes.
    /// </summary>
    IDisposable SubscribeWire(string eventWireName, Action<Type, object> handler);

    /// <summary>Gets the number of wire subscriptions for <paramref name="eventWireName"/> (diagnostics and tests).</summary>
    int CountWireSubscriptions(string eventWireName);
}
