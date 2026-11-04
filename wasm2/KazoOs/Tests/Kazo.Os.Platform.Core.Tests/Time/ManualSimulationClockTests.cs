using Kazo.Os.Platform.Core.Time;

namespace Kazo.Os.Platform.Core.Tests.Time;

public sealed class ManualSimulationClockTests
{
    [Fact]
    public void Advance_runs_due_callbacks_in_due_tick_then_insertion_order()
    {
        ManualSimulationClock clock = new(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1));
        List<string> order = [];

        clock.Schedule(TimeSpan.FromSeconds(2), () => order.Add("second-at-2"));
        clock.Schedule(TimeSpan.FromSeconds(1), () => order.Add("first-at-1"));
        clock.Schedule(TimeSpan.FromSeconds(1), () => order.Add("second-at-1"));

        clock.Advance(2);

        Assert.Equal(["first-at-1", "second-at-1", "second-at-2"], order);
        Assert.Equal(2, clock.CurrentTick);
        Assert.Equal(DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(2), clock.UtcNow);
    }

    [Fact]
    public void Disposing_a_schedule_handle_before_it_is_due_cancels_it()
    {
        ManualSimulationClock clock = new(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1));
        bool ran = false;

        IDisposable handle = clock.Schedule(TimeSpan.FromSeconds(1), () => ran = true);
        handle.Dispose();
        clock.Advance(1);

        Assert.False(ran);
    }

    [Fact]
    public void A_faulting_callback_is_isolated_and_does_not_block_later_callbacks()
    {
        ManualSimulationClock clock = new(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1));
        bool laterRan = false;

        clock.Schedule(TimeSpan.FromSeconds(1), () => throw new InvalidOperationException("boom"));
        clock.Schedule(TimeSpan.FromSeconds(1), () => laterRan = true);

        clock.Advance(1);

        Assert.True(laterRan);
        Assert.Single(clock.Faults);
        Assert.Equal(1, clock.Faults[0].Tick);
        Assert.IsType<InvalidOperationException>(clock.Faults[0].Exception);
    }

    [Fact]
    public async Task DelayAsync_completes_only_after_the_clock_reaches_the_due_tick()
    {
        ManualSimulationClock clock = new(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1));

        Task delay = clock.DelayAsync(TimeSpan.FromSeconds(2));
        clock.Advance(1);
        Assert.False(delay.IsCompleted);

        clock.Advance(1);
        await delay;

        Assert.True(delay.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DelayAsync_is_cancelled_when_the_token_cancels_before_the_due_tick()
    {
        ManualSimulationClock clock = new(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1));
        using CancellationTokenSource cts = new();

        Task delay = clock.DelayAsync(TimeSpan.FromSeconds(5), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => delay);
    }
}
