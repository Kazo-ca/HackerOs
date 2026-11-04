using System.Diagnostics;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Blazor.Windows;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Core;

namespace HackerOs.BrowserHarness.Tests;

/// <summary>
/// One app window ("Calculatrice") whose process the "unresponsive" scenario can hang, freeze or
/// recover, behind the real <see cref="WindowResponsivenessMonitor"/> (ADR 0127). The CPU accountant
/// and the process table are scripted: this harness has no Kazo OS kernel.
/// </summary>
public sealed class UnresponsiveScenarioState
{
    /// <summary>Threshold of the scenario, short so the browser tests stay fast.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(600);

    /// <summary>The scenario's only app window.</summary>
    public static readonly WindowId WindowId = WindowId.FromGuid(Guid.Parse("40000000-0000-0000-0000-000000000001"));

    private static readonly AppInstanceId Instance = AppInstanceId.FromGuid(Guid.Parse("50000000-0000-0000-0000-000000000001"));

    /// <summary>Creates the window, its process and the monitor.</summary>
    public UnresponsiveScenarioState()
    {
        Runtime = new WindowRuntime(new WindowBounds(0, 0, 960, 640));
        Process = new ProcessRecord(
            ProcessId.FromInt64(42), parentPid: null, "org.hackeros.calculator", Instance, AppKind.Window,
            LocalUserId.FromGuid(Guid.NewGuid()), SessionId.FromGuid(Guid.NewGuid()), ProcessState.Running,
            ResourceProfile.None, serviceHealth: null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            stoppedAtUtc: null, exitCode: null, exitReason: null);
        Processes = new ScriptedProcessTable(this);
        Accountant = new ScriptedAccountant();
        Monitor = new WindowResponsivenessMonitor(Runtime, Processes, Accountant, Threshold, TimeProvider.System, settings: null, logger: null);
        Runtime.Apply(new CreateWindowCommand(new WindowRuntimeState(
            WindowId,
            "org.hackeros.calculator",
            WindowOwnerId.FromGuid(Instance.Value),
            "Calculatrice",
            null,
            new WindowBounds(80, 60, 520, 380),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240))));
    }

    /// <summary>Gets the window runtime.</summary>
    public WindowRuntime Runtime { get; }

    /// <summary>Gets the window's process.</summary>
    public ProcessRecord Process { get; }

    /// <summary>Gets the scripted process table.</summary>
    public ScriptedProcessTable Processes { get; }

    /// <summary>Gets the scripted CPU accountant.</summary>
    public ScriptedAccountant Accountant { get; }

    /// <summary>Gets the monitor under test.</summary>
    public WindowResponsivenessMonitor Monitor { get; }

    /// <summary>Gets whether the process was ended ("Terminer l'application").</summary>
    public bool IsTerminated { get; private set; }

    /// <summary>Raised when the process ends.</summary>
    public event Action? Terminated;

    /// <summary>
    /// Really holds the browser's main thread for <paramref name="duration"/>, charged to the
    /// process as one slice longer than the threshold (ADR 0067), like an app looping in a click handler.
    /// </summary>
    public void Freeze(TimeSpan duration)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < duration)
        {
            // Busy, on purpose: this is the frozen app.
        }

        Accountant.Charge(watch.Elapsed, slices: 1, responding: false);
    }

    internal void Terminate()
    {
        IsTerminated = true;
        Accountant.Hung = false;
        if (Runtime.Windows.Any(window => window.Id == WindowId))
        {
            Runtime.Apply(new ForceWindowCloseCommand(WindowId));
        }

        Terminated?.Invoke();
    }

    /// <summary>CPU times of the one process, scripted by the scenario's buttons.</summary>
    public sealed class ScriptedAccountant : ICpuTimeAccountant
    {
        private ProcessCpuTimes _times = new(TimeSpan.Zero, TimeSpan.Zero, 0, TimeSpan.Zero, true, 0);
        private readonly Stopwatch _sinceRead = Stopwatch.StartNew();

        /// <summary>
        /// Gets or sets whether the process is stuck in one long slice that is still running, as a
        /// worker's heartbeat would report it: its CPU time keeps growing and it does not respond.
        /// </summary>
        public bool Hung { get; set; }

        /// <inheritdoc />
        public bool IsMeasuring => true;

        /// <summary>Adds a completed stretch of work.</summary>
        public void Charge(TimeSpan busy, int slices, bool responding) =>
            _times = _times with
            {
                UserTime = _times.UserTime + busy,
                IsResponding = responding,
                VoluntaryContextSwitches = _times.VoluntaryContextSwitches + slices
            };

        /// <inheritdoc />
        public bool TryGetCpuUsage(ProcessId pid, out double coreEquivalents)
        {
            coreEquivalents = Hung ? 1 : 0;
            return true;
        }

        /// <inheritdoc />
        public bool TryGetProcessTimes(ProcessId pid, out ProcessCpuTimes times)
        {
            if (Hung)
            {
                _times = _times with { UserTime = _times.UserTime + _sinceRead.Elapsed, IsResponding = false };
            }

            _sinceRead.Restart();
            times = _times;
            return true;
        }

        /// <inheritdoc />
        public bool TryGetThreadTimes(out MeasuredThreadCpuTimes times)
        {
            times = default;
            return false;
        }
    }

    /// <summary>Process table of the one process; a stop ends it at once.</summary>
    public sealed class ScriptedProcessTable(UnresponsiveScenarioState owner) : IProcessManager
    {
        /// <summary>Gets the reasons of the stops received.</summary>
        public List<(TimeSpan Timeout, ProcessExitReason Reason)> Stops { get; } = [];

        /// <inheritdoc />
        public IReadOnlyList<ProcessRecord> GetActiveProcesses() => owner.IsTerminated ? [] : [owner.Process];

        /// <inheritdoc />
        public IReadOnlyList<ProcessRecord> GetHistory() => [];

        /// <inheritdoc />
        public bool TryGetActive(ProcessId pid, out ProcessRecord process)
        {
            process = owner.Process;
            return !owner.IsTerminated && pid == owner.Process.Pid;
        }

        /// <inheritdoc />
        public ProcessRecord Start(ProcessStartRequest request) => throw new NotSupportedException();

        /// <inheritdoc />
        public ProcessRecord MarkRunning(ProcessId pid) => throw new NotSupportedException();

        /// <inheritdoc />
        public ProcessRecord Complete(ProcessId pid, int exitCode) => throw new NotSupportedException();

        /// <inheritdoc />
        public ProcessRecord Fault(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Fault) => throw new NotSupportedException();

        /// <inheritdoc />
        public Task<ProcessRecord> StopAsync(
            ProcessId pid, TimeSpan timeout, ProcessExitReason reason = ProcessExitReason.CloseRequested, CancellationToken cancellationToken = default)
        {
            Stops.Add((timeout, reason));
            owner.Terminate();
            return Task.FromResult(owner.Process);
        }

        /// <inheritdoc />
        public ProcessRecord Kill(ProcessId pid, ProcessExitReason reason = ProcessExitReason.Killed) => throw new NotSupportedException();

        /// <inheritdoc />
        public bool TryGetSingleton(string appId, out ProcessRecord process) => throw new NotSupportedException();

        /// <inheritdoc />
        public CancellationToken GetCancellationToken(ProcessId pid) => throw new NotSupportedException();
    }
}
