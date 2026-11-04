using Kazo.Os.Platform.Core.Time;

namespace Kazo.Os.Platform.Core.Tests.Time;

public sealed class SimulationClockDriverTests
{
    [Fact]
    public void Pump_advances_whole_elapsed_ticks_and_carries_the_remainder()
    {
        ManualSimulationClock clock = new(DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(100));
        SteppingTimeProvider time = new();
        using SimulationClockDriver driver = new(clock, time);
        driver.Start();

        time.Step(TimeSpan.FromMilliseconds(250));
        Assert.Equal(2, driver.Pump());
        time.Step(TimeSpan.FromMilliseconds(50));
        Assert.Equal(1, driver.Pump());

        Assert.Equal(3, clock.CurrentTick);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMilliseconds(300), clock.UtcNow);
    }

    [Fact]
    public void A_throttled_beat_is_capped_and_the_rest_is_dropped()
    {
        ManualSimulationClock clock = new(DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(100));
        SteppingTimeProvider time = new();
        using SimulationClockDriver driver = new(clock, time, maxTicksPerBeat: 10);
        driver.Start();

        time.Step(TimeSpan.FromMinutes(5));
        Assert.Equal(10, driver.Pump());
        time.Step(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, driver.Pump());
        Assert.Equal(11, clock.CurrentTick);
    }

    [Fact]
    public void Scheduled_callbacks_run_once_the_driver_advances_past_them()
    {
        ManualSimulationClock clock = new(DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(100));
        SteppingTimeProvider time = new();
        using SimulationClockDriver driver = new(clock, time);
        bool ran = false;
        clock.Schedule(TimeSpan.FromSeconds(1), () => ran = true);
        driver.Start();

        time.Step(TimeSpan.FromMilliseconds(900));
        driver.Pump();
        Assert.False(ran);
        time.Step(TimeSpan.FromMilliseconds(100));
        driver.Pump();
        Assert.True(ran);
    }

    [Fact]
    public void Start_is_idempotent_and_dispose_stops_the_timer()
    {
        ManualSimulationClock clock = new(DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(100));
        SteppingTimeProvider time = new();
        SimulationClockDriver driver = new(clock, time);

        driver.Start();
        driver.Start();
        Assert.True(driver.IsRunning);
        Assert.Equal(1, time.TimersCreated);

        driver.Dispose();
        Assert.False(driver.IsRunning);
    }

    /// <summary>A TimeProvider whose clock only moves when the test steps it; timers never fire on their own.</summary>
    private sealed class SteppingTimeProvider : TimeProvider
    {
        private long _timestamp;

        public int TimersCreated { get; private set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Step(TimeSpan delta) => _timestamp += delta.Ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimersCreated++;
            return new InertTimer();
        }

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
