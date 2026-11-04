using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Tests.Support;

/// <summary>Fixed-time <see cref="IAppClockGateway"/> double for command tests.</summary>
public sealed class FakeAppClockGateway(DateTimeOffset utcNow) : IAppClockGateway
{
    public DateTimeOffset UtcNow { get; } = utcNow;

    public long CurrentTick => 0;

    public IDisposable Schedule(TimeSpan delay, Action callback) =>
        throw new NotSupportedException("Schedule is not implemented by this fake.");

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("DelayAsync is not implemented by this fake.");
}
