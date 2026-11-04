namespace Kazo.Os.Platform.Core.Time;

/// <summary>
/// The production "browser-aware adapter" of ADR 0012: advances a <see cref="ManualSimulationClock"/>
/// from one platform timer so scheduled callbacks (taskbar clock, process stop timeouts, the
/// <c>/var/log</c> flush of ADR 0045, app <c>Clock.DelayAsync</c>) actually run in a live host.
/// </summary>
/// <remarks>
/// Each timer beat converts the real time elapsed since the previous beat into whole ticks. A
/// throttled background tab wakes up with a large elapsed time; the driver then advances at most
/// <see cref="MaxTicksPerBeat"/> ticks and forgets the rest, rather than pretending every missed
/// interval executed (ADR 0012). Domain code never sees this timer, only the clock.
/// </remarks>
public sealed class SimulationClockDriver : IDisposable
{
    /// <summary>Default cap on ticks advanced by one beat: five seconds at the 100 ms host tick.</summary>
    public const int DefaultMaxTicksPerBeat = 50;

    private readonly ManualSimulationClock _clock;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private long _lastTimestamp;
    private TimeSpan _carry;
    private int _pumping;

    /// <summary>Initializes a stopped driver for <paramref name="clock"/>.</summary>
    /// <param name="clock">Clock to advance.</param>
    /// <param name="timeProvider">Source of real elapsed time and of the platform timer.</param>
    /// <param name="maxTicksPerBeat">Maximum ticks advanced by one beat; at least one.</param>
    public SimulationClockDriver(
        ManualSimulationClock clock,
        TimeProvider timeProvider,
        int maxTicksPerBeat = DefaultMaxTicksPerBeat)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTicksPerBeat, 1);
        MaxTicksPerBeat = maxTicksPerBeat;
    }

    /// <summary>Gets the maximum ticks one beat may advance.</summary>
    public int MaxTicksPerBeat { get; }

    /// <summary>Gets whether the platform timer is running.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _timer is not null;
            }
        }
    }

    /// <summary>Starts the platform timer, beating once per clock tick. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_timer is not null)
            {
                return;
            }

            _lastTimestamp = _timeProvider.GetTimestamp();
            _carry = TimeSpan.Zero;
            _timer = _timeProvider.CreateTimer(
                static state => ((SimulationClockDriver)state!).Pump(),
                this,
                _clock.TickDuration,
                _clock.TickDuration);
        }
    }

    /// <summary>
    /// Runs one beat: advances the clock by the whole ticks of real time elapsed since the last
    /// beat, capped at <see cref="MaxTicksPerBeat"/>. Called by the timer; public for tests and for
    /// hosts that want to pump explicitly. A beat arriving while another runs is skipped.
    /// </summary>
    /// <returns>The number of ticks advanced.</returns>
    public int Pump()
    {
        if (Interlocked.Exchange(ref _pumping, 1) == 1)
        {
            return 0;
        }

        try
        {
            int ticks;
            lock (_gate)
            {
                long now = _timeProvider.GetTimestamp();
                TimeSpan elapsed = _timeProvider.GetElapsedTime(_lastTimestamp, now) + _carry;
                _lastTimestamp = now;

                long dueTicks = elapsed.Ticks / _clock.TickDuration.Ticks;
                if (dueTicks > MaxTicksPerBeat)
                {
                    // Throttled tab: run a bounded catch-up and drop the remainder.
                    ticks = MaxTicksPerBeat;
                    _carry = TimeSpan.Zero;
                }
                else
                {
                    ticks = (int)dueTicks;
                    _carry = elapsed - (_clock.TickDuration * ticks);
                }
            }

            if (ticks > 0)
            {
                _clock.Advance(ticks);
            }

            return ticks;
        }
        finally
        {
            Volatile.Write(ref _pumping, 0);
        }
    }

    /// <summary>Stops the platform timer. The clock keeps its current time.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
