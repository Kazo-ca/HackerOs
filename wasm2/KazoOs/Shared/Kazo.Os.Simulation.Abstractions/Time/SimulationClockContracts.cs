namespace Kazo.Os.Simulation.Abstractions.Time;

/// <summary>
/// Provides deterministic simulation time in place of <see cref="DateTimeOffset.UtcNow"/>,
/// <see cref="Task.Delay(TimeSpan)"/>, or browser timers.
/// </summary>
/// <remarks>
/// Domain code depends on this abstraction so identical seeds, schedules, and tick
/// advancement always produce identical ordering, matching ADR 0012.
/// </remarks>
public interface ISimulationClock
{
    /// <summary>Gets the current simulated UTC time.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Gets the current monotonic tick number, starting at zero.</summary>
    long CurrentTick { get; }

    /// <summary>Gets the fixed duration represented by one tick.</summary>
    TimeSpan TickDuration { get; }

    /// <summary>
    /// Schedules a callback to run once simulated time reaches at least <paramref name="delay"/>
    /// from now.
    /// </summary>
    /// <param name="delay">Minimum simulated delay before the callback runs; zero runs at the next tick.</param>
    /// <param name="callback">Callback invoked on the tick it becomes due.</param>
    /// <returns>A handle that cancels the callback if it has not already run.</returns>
    IDisposable Schedule(TimeSpan delay, Action callback);

    /// <summary>
    /// Returns a task that completes once simulated time reaches at least <paramref name="delay"/>
    /// from now, or the token is cancelled.
    /// </summary>
    /// <param name="delay">Minimum simulated delay before completion.</param>
    /// <param name="cancellationToken">Token that cancels the pending delay.</param>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

/// <summary>Records one scheduled callback that threw an exception when it ran.</summary>
/// <param name="Tick">Tick on which the callback ran and faulted.</param>
/// <param name="Exception">Exception thrown by the callback.</param>
public sealed record SimulationSchedulerFault(long Tick, Exception Exception);
