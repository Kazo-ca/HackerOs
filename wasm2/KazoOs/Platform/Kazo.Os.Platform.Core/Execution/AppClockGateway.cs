using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>Provides one app instance's read-only deterministic simulation clock access.</summary>
public sealed class AppClockGateway : IAppClockGateway
{
    private readonly ISimulationClock _clock;
    private readonly AppAmbientContextBinding _ambient = new();

    /// <summary>Initializes a clock gateway bound to the shared simulation clock.</summary>
    public AppClockGateway(ISimulationClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow => _clock.UtcNow;

    /// <inheritdoc />
    public long CurrentTick => _clock.CurrentTick;

    /// <inheritdoc />
    /// <remarks>The callback runs inside this app's <see cref="Kazo.Os.AppSdk.HackExecutionContext"/> scope (ADR 0043).</remarks>
    public IDisposable Schedule(TimeSpan delay, Action callback) => _clock.Schedule(delay, _ambient.Wrap(callback));

    /// <summary>Binds the app context scheduled callbacks run under; called once by the factory.</summary>
    internal void BindAmbientContext(Kazo.Os.AppSdk.IAppExecutionContext context) => _ambient.Bind(context);

    /// <inheritdoc />
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        _clock.DelayAsync(delay, cancellationToken);
}
