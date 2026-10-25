using HackerOs.Simulation.Abstractions.Events;

namespace HackerOs.Samples.ServiceApp;

/// <summary>
/// Names the topic <see cref="SampleTickerService"/> publishes <see cref="SampleTickerEvent"/> on. The
/// first real example of the app-owned topic lane described in
/// <c>doc/en/wasm/dev/adr/0038-emitter-authorized-topic-messaging.md</c> — built only through <see cref="TopicNames"/>,
/// never a hand-typed string.
/// </summary>
public static class SampleTickerTopics
{
    /// <summary>Gets the topic each tick's <see cref="SampleTickerEvent"/> is published on.</summary>
    public static TopicName Ticked { get; } =
        TopicNames.ForApp(SampleTickerService.AppId).Segment("ticked").Build();
}
