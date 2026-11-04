using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Windowing.Core;

namespace Kazo.Os.Platform.Blazor.Tests;

/// <summary>
/// ADR 0127 §2-3: the three cases that mark a window "not responding", what ends the state, and the
/// "Attendre | Terminer l'application" dialog, on a manual clock and a scripted CPU accountant.
/// </summary>
public sealed class WindowResponsivenessMonitorTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);

    [Fact]
    public void Case_1_a_process_not_responding_and_still_busy_marks_its_window_and_opens_the_dialog()
    {
        Harness harness = new();
        harness.Monitor.Evaluate();

        harness.Run(TimeSpan.FromSeconds(6), slices: 1, responding: false);
        harness.Monitor.Evaluate();

        Assert.False(harness.Window.IsResponding);
        Assert.True(harness.Monitor.IsDialogOpen(harness.WindowId));
        Assert.Equal([harness.WindowId], harness.Monitor.UnresponsiveWindows);
    }

    [Fact]
    public void A_stale_flag_of_an_idle_process_marks_nothing()
    {
        Harness harness = new();
        harness.Run(TimeSpan.FromSeconds(6), slices: 1, responding: false);

        harness.Monitor.Evaluate();
        harness.Advance(TimeSpan.FromSeconds(1));
        harness.Monitor.Evaluate();

        Assert.True(harness.Window.IsResponding);
    }

    [Fact]
    public void Case_2_an_input_that_waited_on_this_process_marks_the_window()
    {
        Harness harness = new();
        harness.Monitor.Evaluate();

        // Nothing could run while the app held the thread for 7 s; its slice may still be open.
        harness.Run(TimeSpan.FromSeconds(7), slices: 0, responding: true);
        harness.Monitor.ReportInput(harness.WindowId, TimeSpan.FromSeconds(6));

        Assert.False(harness.Window.IsResponding);
        Assert.True(harness.Monitor.IsDialogOpen(harness.WindowId));
    }

    [Fact]
    public void Case_2_an_input_that_waited_on_another_process_does_not_mark_the_window()
    {
        Harness harness = new();
        harness.Monitor.Evaluate();

        harness.Advance(TimeSpan.FromSeconds(7));
        harness.Monitor.ReportInput(harness.WindowId, TimeSpan.FromSeconds(6));

        Assert.True(harness.Window.IsResponding);
        Assert.False(harness.Monitor.IsDialogOpen(harness.WindowId));
    }

    [Fact]
    public void Case_3_an_input_given_during_a_long_slice_shows_the_dialog_when_the_slice_ends()
    {
        Harness harness = new();
        harness.Monitor.Evaluate();

        // The user clicked near the end of a 5.5 s slice: the click itself only waited 300 ms.
        harness.Run(TimeSpan.FromSeconds(5.5), slices: 1, responding: false);
        harness.Monitor.ReportInput(harness.WindowId, TimeSpan.FromMilliseconds(300));

        Assert.False(harness.Window.IsResponding);
        Assert.True(harness.Monitor.IsDialogOpen(harness.WindowId));
    }

    [Fact]
    public void A_fast_input_ends_the_state_and_closes_the_dialog()
    {
        Harness harness = MarkedHarness();
        Assert.False(harness.Window.IsResponding);

        harness.Advance(TimeSpan.FromMilliseconds(500));
        harness.Monitor.ReportInput(harness.WindowId, TimeSpan.FromMilliseconds(12));

        Assert.True(harness.Window.IsResponding);
        Assert.False(harness.Monitor.IsDialogOpen(harness.WindowId));

        // The accountant's flag is still the old one: the same slice does not mark the window again.
        harness.Advance(TimeSpan.FromSeconds(1));
        harness.Monitor.Evaluate();
        Assert.True(harness.Window.IsResponding);
    }

    [Fact]
    public void A_short_slice_completed_by_the_process_ends_the_state()
    {
        Harness harness = MarkedHarness();

        harness.Run(TimeSpan.FromMilliseconds(3), slices: 1, responding: true);
        harness.Monitor.Evaluate();

        Assert.True(harness.Window.IsResponding);
    }

    [Fact]
    public void A_process_idle_for_a_whole_threshold_answers_again()
    {
        Harness harness = MarkedHarness();

        harness.Advance(TimeSpan.FromSeconds(3));
        harness.Monitor.Evaluate();
        Assert.False(harness.Window.IsResponding);

        harness.Advance(TimeSpan.FromSeconds(3));
        harness.Monitor.Evaluate();
        Assert.True(harness.Window.IsResponding);
    }

    [Fact]
    public void Wait_closes_the_dialog_and_it_comes_back_after_the_threshold_if_still_frozen()
    {
        Harness harness = MarkedHarness();

        harness.Monitor.Wait(harness.WindowId);
        Assert.False(harness.Monitor.IsDialogOpen(harness.WindowId));
        Assert.False(harness.Window.IsResponding);

        harness.Run(TimeSpan.FromSeconds(4), slices: 0, responding: false);
        harness.Monitor.Evaluate();
        Assert.False(harness.Monitor.IsDialogOpen(harness.WindowId));

        harness.Run(TimeSpan.FromSeconds(1.5), slices: 0, responding: false);
        harness.Monitor.Evaluate();
        Assert.True(harness.Monitor.IsDialogOpen(harness.WindowId));
    }

    [Fact]
    public void The_close_button_of_a_frozen_window_opens_the_dialog_instead_of_closing()
    {
        Harness harness = new();
        Assert.False(harness.Monitor.InterceptClose(harness.WindowId));

        harness = MarkedHarness();
        harness.Monitor.Wait(harness.WindowId);

        Assert.True(harness.Monitor.InterceptClose(harness.WindowId));
        Assert.True(harness.Monitor.IsDialogOpen(harness.WindowId));
    }

    [Fact]
    public async Task Terminate_asks_the_process_to_stop_with_a_two_second_grace()
    {
        Harness harness = MarkedHarness();

        await harness.Monitor.TerminateAsync(harness.WindowId);

        (ProcessId pid, TimeSpan timeout, ProcessExitReason reason) = Assert.Single(harness.Processes.Stops);
        Assert.Equal(harness.Process.Pid, pid);
        Assert.Equal(TimeSpan.FromSeconds(2), timeout);
        Assert.Equal(ProcessExitReason.Terminated, reason);
        Assert.False(harness.Monitor.IsDialogOpen(harness.WindowId));
    }

    [Fact]
    public void Without_cpu_measurement_no_window_is_ever_marked()
    {
        Harness harness = new(measuring: false);
        harness.Monitor.Evaluate();

        harness.Run(TimeSpan.FromSeconds(7), slices: 1, responding: false);
        harness.Monitor.Evaluate();
        harness.Monitor.ReportInput(harness.WindowId, TimeSpan.FromSeconds(6));

        Assert.True(harness.Window.IsResponding);
        Assert.False(harness.Monitor.IsMeasuring);
    }

    [Fact]
    public void Windows_without_an_app_process_are_left_alone()
    {
        Harness harness = new(registerProcess: false);
        harness.Monitor.Evaluate();
        harness.Run(TimeSpan.FromSeconds(7), slices: 1, responding: false);

        harness.Monitor.Evaluate();
        harness.Monitor.ReportInput(harness.WindowId, TimeSpan.FromSeconds(6));

        Assert.True(harness.Window.IsResponding);
    }

    /// <summary>A harness whose window was just marked by case 1, with its dialog open.</summary>
    private static Harness MarkedHarness()
    {
        Harness harness = new();
        harness.Monitor.Evaluate();
        harness.Run(TimeSpan.FromSeconds(6), slices: 1, responding: false);
        harness.Monitor.Evaluate();
        Assert.True(harness.Monitor.IsDialogOpen(harness.WindowId));
        return harness;
    }

    private sealed class Harness
    {
        private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        private readonly ScriptedAccountant _accountant;

        public Harness(bool measuring = true, bool registerProcess = true)
        {
            WindowRuntime = new WindowRuntime(new WindowBounds(0, 0, 1280, 720));
            AppInstanceId instance = AppInstanceId.FromGuid(Guid.NewGuid());
            Process = new ProcessRecord(
                ProcessId.FromInt64(42), parentPid: null, "org.hackeros.calculator", instance, AppKind.Window,
                LocalUserId.FromGuid(Guid.NewGuid()), SessionId.FromGuid(Guid.NewGuid()), ProcessState.Running,
                ResourceProfile.None, serviceHealth: null, _time.GetUtcNow(), _time.GetUtcNow(),
                stoppedAtUtc: null, exitCode: null, exitReason: null);
            Processes = new RecordingProcessManager(registerProcess ? [Process] : []);
            _accountant = new ScriptedAccountant(measuring);
            WindowId = WindowId.FromGuid(Guid.NewGuid());
            WindowRuntime.Apply(new CreateWindowCommand(new WindowRuntimeState(
                WindowId,
                "org.hackeros.calculator",
                WindowOwnerId.FromGuid(instance.Value),
                "Calculatrice",
                null,
                new WindowBounds(20, 20, 640, 480),
                null,
                0,
                WindowVisualState.Normal,
                new WindowConstraints(true, 320, 240))));
            Monitor = new WindowResponsivenessMonitor(
                WindowRuntime, Processes, _accountant, Threshold, _time, settings: null, logger: null);
        }

        public WindowRuntime WindowRuntime { get; }

        public WindowId WindowId { get; }

        public ProcessRecord Process { get; }

        public RecordingProcessManager Processes { get; }

        public WindowResponsivenessMonitor Monitor { get; }

        public WindowRuntimeState Window => WindowRuntime.Windows.Single(window => window.Id == WindowId);

        /// <summary>Lets real time pass while the process does nothing.</summary>
        public void Advance(TimeSpan elapsed) => _time.Advance(elapsed);

        /// <summary>The process holds the thread for <paramref name="busy"/> (time passes by as much).</summary>
        public void Run(TimeSpan busy, int slices, bool responding)
        {
            _time.Advance(busy);
            _accountant.Times = _accountant.Times with
            {
                UserTime = _accountant.Times.UserTime + busy,
                IsResponding = responding,
                VoluntaryContextSwitches = _accountant.Times.VoluntaryContextSwitches + slices
            };
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class ScriptedAccountant(bool measuring) : ICpuTimeAccountant
    {
        public ProcessCpuTimes Times { get; set; } = new(TimeSpan.Zero, TimeSpan.Zero, 0, TimeSpan.Zero, true, 0);

        public bool IsMeasuring => measuring;

        public bool TryGetCpuUsage(ProcessId pid, out double coreEquivalents)
        {
            coreEquivalents = 0;
            return measuring;
        }

        public bool TryGetProcessTimes(ProcessId pid, out ProcessCpuTimes times)
        {
            times = Times;
            return measuring;
        }

        public bool TryGetThreadTimes(out MeasuredThreadCpuTimes times)
        {
            times = default;
            return false;
        }
    }

    /// <summary>Process table double: lists its processes and records stop requests.</summary>
    private sealed class RecordingProcessManager(IReadOnlyList<ProcessRecord> active) : IProcessManager
    {
        public List<(ProcessId Pid, TimeSpan Timeout, ProcessExitReason Reason)> Stops { get; } = [];

        public IReadOnlyList<ProcessRecord> GetActiveProcesses() => active;

        public IReadOnlyList<ProcessRecord> GetHistory() => [];

        public bool TryGetActive(ProcessId pid, out ProcessRecord process)
        {
            process = active.FirstOrDefault(candidate => candidate.Pid == pid)!;
            return process is not null;
        }

        public ProcessRecord Start(ProcessStartRequest request) => throw new NotSupportedException();

        public ProcessRecord MarkRunning(ProcessId pid) => throw new NotSupportedException();

        public ProcessRecord Complete(ProcessId pid, int exitCode) => throw new NotSupportedException();

        public ProcessRecord Fault(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Fault) =>
            throw new NotSupportedException();

        public Task<ProcessRecord> StopAsync(
            ProcessId pid, TimeSpan timeout, ProcessExitReason reason = ProcessExitReason.CloseRequested, CancellationToken cancellationToken = default)
        {
            Stops.Add((pid, timeout, reason));
            return Task.FromResult(active.First(candidate => candidate.Pid == pid));
        }

        public ProcessRecord Kill(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Killed) =>
            throw new NotSupportedException();

        public bool TryGetSingleton(string appId, out ProcessRecord process) => throw new NotSupportedException();

        public CancellationToken GetCancellationToken(ProcessId pid) => throw new NotSupportedException();
    }
}
