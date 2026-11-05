using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.ResourceAccounting.Tests;

public sealed class ContextSwitchDispatcherTests
{
    private static readonly ExecutionContextSwitch Switch = new(null, ProcessId.FromInt64(1), false);

    [Fact]
    public void Every_registered_measurement_sees_the_switch()
    {
        ContextSwitchDispatcher dispatcher = new();
        RecordingObserver cpu = new();
        RecordingObserver memory = new();
        dispatcher.Register(cpu);
        dispatcher.Register(memory);

        dispatcher.OnContextSwitch(Switch);

        Assert.Equal([Switch], cpu.Switches);
        Assert.Equal([Switch], memory.Switches);
        Assert.Equal(2, dispatcher.ObserverCount);
    }

    [Fact]
    public void An_unregistered_measurement_no_longer_sees_switches()
    {
        ContextSwitchDispatcher dispatcher = new();
        RecordingObserver observer = new();
        IDisposable registration = dispatcher.Register(observer);

        registration.Dispose();
        registration.Dispose();
        dispatcher.OnContextSwitch(Switch);

        Assert.Empty(observer.Switches);
        Assert.Equal(0, dispatcher.ObserverCount);
    }

    [Fact]
    public void A_throwing_measurement_does_not_stop_the_others()
    {
        ContextSwitchDispatcher dispatcher = new();
        RecordingObserver after = new();
        dispatcher.Register(new ThrowingObserver());
        dispatcher.Register(after);

        dispatcher.OnContextSwitch(Switch);

        Assert.Equal([Switch], after.Switches);
    }

    private sealed class ThrowingObserver : IExecutionContextSwitchObserver
    {
        public void OnContextSwitch(in ExecutionContextSwitch contextSwitch) =>
            throw new InvalidOperationException("observer failure");
    }
}
