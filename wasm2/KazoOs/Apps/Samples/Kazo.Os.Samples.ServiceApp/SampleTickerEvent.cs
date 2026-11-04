namespace Kazo.Os.Samples.ServiceApp;

/// <summary>
/// Event published on every tick of the <see cref="SampleTickerService"/>.
/// </summary>
/// <param name="TickCount">Total number of ticks executed during the current active session.</param>
/// <param name="SimulatedTime">Simulated UTC timestamp of the tick.</param>
/// <param name="Status">Service health status description.</param>
public sealed record SampleTickerEvent(
    long TickCount,
    DateTimeOffset SimulatedTime,
    string Status);
