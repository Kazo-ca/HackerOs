using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Platform.Core.Shell;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Windowing.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Blazor.Windows;

/// <summary>
/// Decides which app windows do not answer their inputs (ADR 0127 §2), the way a window manager
/// pings its clients (<c>_NET_WM_PING</c> on X11, <c>IsHungAppWindow</c> on Windows), and drives the
/// "« App » ne répond pas : Attendre | Terminer l'application" dialog of ADR 0127 §3.
/// </summary>
/// <remarks>
/// <para>
/// Every process of Kazo OS that renders a window runs on the browser's main thread, which it
/// shares with the window manager itself (ADR 0067). While an app holds that thread nothing else
/// runs, not even this monitor: the verdict is therefore reached as soon as the thread is given
/// back, from what the CPU accountant measured and from how long the user's inputs waited. A window
/// is judged unresponsive when one of the three cases of ADR 0127 §2 holds:
/// </para>
/// <list type="number">
/// <item><description>
/// its process is "Not responding" for the accountant (a slice longer than the threshold) and kept
/// running since the previous sample (<see cref="Evaluate"/>);
/// </description></item>
/// <item><description>
/// an input delivered to the window waited longer than the threshold while, for at least half that
/// wait, the window's own process held the thread (<see cref="ReportInput"/>);
/// </description></item>
/// <item><description>
/// a slice longer than the threshold just ended and the user acted on the window during it: the
/// input reported right after that slice (<see cref="ReportInput"/>).
/// </description></item>
/// </list>
/// <para>
/// The state ends when the window answers an input under the threshold, when the accountant sees the
/// process complete a short slice again, or when the process stayed idle for a whole threshold (an
/// idle process answers at once). Without CPU measurement (<see cref="ICpuTimeAccountant.IsMeasuring"/>
/// false) nothing can be attributed to a process and no window is ever marked.
/// </para>
/// <para>
/// The monitor holds no timer and no JavaScript: <c>WindowResponsivenessHost</c> calls
/// <see cref="Evaluate"/> every second and forwards the input latencies its probe measures, so the
/// rules here run under a fake <see cref="TimeProvider"/> in unit tests.
/// </para>
/// </remarks>
public sealed class WindowResponsivenessMonitor : IDisposable
{
    /// <summary>
    /// Inputs that waited at least this long are worth reporting from the browser: the browser's own
    /// long-task threshold (ADR 0067 §4). Shorter waits only matter for a window already marked.
    /// </summary>
    public static readonly TimeSpan LongInputDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Grace period of "Terminer l'application" before the kill (ADR 0127 §3, ADR 0069).</summary>
    public static readonly TimeSpan TerminateGracePeriod = TimeSpan.FromSeconds(2);

    private readonly WindowRuntime _runtime;
    private readonly IProcessManager _processes;
    private readonly ICpuTimeAccountant _accountant;
    private readonly TimeProvider _time;
    private readonly IOsSettingsService? _settings;
    private readonly ILogger _logger;
    private readonly Dictionary<WindowId, WindowWatch> _watches = [];
    private bool _started;
    private bool _disposed;

    /// <summary>Creates a monitor with the default threshold, the system clock and no settings file.</summary>
    /// <param name="runtime">Authoritative window runtime, whose windows are marked.</param>
    /// <param name="processes">Process table: the owner of each window and the termination path.</param>
    /// <param name="accountant">Measured CPU times of each process (ADR 0067).</param>
    public WindowResponsivenessMonitor(WindowRuntime runtime, IProcessManager processes, ICpuTimeAccountant accountant)
        : this(runtime, processes, accountant, Kazo.Os.Platform.Core.Processes.Accounting.ResourceAccountingSettings.DefaultUnresponsiveThreshold, TimeProvider.System, settings: null, logger: null)
    {
    }

    /// <summary>Creates a monitor.</summary>
    /// <param name="runtime">Authoritative window runtime, whose windows are marked.</param>
    /// <param name="processes">Process table: the owner of each window and the termination path.</param>
    /// <param name="accountant">Measured CPU times of each process (ADR 0067).</param>
    /// <param name="threshold"><c>Cpu:UnresponsiveThresholdMs</c> (ADR 0066), used for all three cases.</param>
    /// <param name="timeProvider">Real-time clock; input latencies are real time too.</param>
    /// <param name="settings">OS settings, to read <c>window-manager.config</c>; without it the dialog stays on.</param>
    /// <param name="logger">Receives a warning when the settings file cannot be read.</param>
    public WindowResponsivenessMonitor(
        WindowRuntime runtime,
        IProcessManager processes,
        ICpuTimeAccountant accountant,
        TimeSpan threshold,
        TimeProvider? timeProvider,
        IOsSettingsService? settings,
        ILogger<WindowResponsivenessMonitor>? logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _accountant = accountant ?? throw new ArgumentNullException(nameof(accountant));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(threshold, TimeSpan.Zero);
        Threshold = threshold;
        _time = timeProvider ?? TimeProvider.System;
        _settings = settings;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _runtime.EventsApplied += OnWindowEvents;
    }

    /// <summary>Raised when a window's state or its dialog changes, so the shell re-renders.</summary>
    public event Action? Changed;

    /// <summary>Gets the threshold of all three cases (<c>Cpu:UnresponsiveThresholdMs</c>).</summary>
    public TimeSpan Threshold { get; }

    /// <summary>
    /// Gets whether the dialog opens on its own over an unresponsive window
    /// (<c>[unresponsive] dialog</c> of <c>window-manager.config</c>, ADR 0127 §5). The close button
    /// of an unresponsive window opens it either way.
    /// </summary>
    public bool IsDialogEnabled { get; private set; } = true;

    /// <summary>Gets whether measured CPU times can attribute a wait to a process.</summary>
    public bool IsMeasuring => _accountant.IsMeasuring;

    /// <summary>Gets the windows currently marked as not responding.</summary>
    public IReadOnlyList<WindowId> UnresponsiveWindows =>
        [.. _watches.Where(pair => pair.Value.IsUnresponsive).Select(pair => pair.Key)];

    /// <summary>Gets whether <paramref name="windowId"/> shows the "not responding" dialog.</summary>
    /// <param name="windowId">The window.</param>
    public bool IsDialogOpen(WindowId windowId) =>
        _watches.TryGetValue(windowId, out WindowWatch? watch) && watch.IsDialogOpen;

    /// <summary>Reads <c>window-manager.config</c> and follows its changes. Safe to call more than once.</summary>
    /// <param name="cancellationToken">Cancels the first read.</param>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started || _settings is null)
        {
            return;
        }

        _started = true;
        _settings.DocumentChanged += OnSettingsChanged;
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _settings.WatchAsync(WindowManagerSettingsDocuments.Document, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "window-manager: changes to window-manager.config are not followed");
        }
    }

    /// <summary>
    /// Samples every app window's process (case 1 of ADR 0127 §2) and ends the state of windows whose
    /// process answers again. Called about once a second by the shell.
    /// </summary>
    public void Evaluate()
    {
        DateTimeOffset now = _time.GetUtcNow();
        bool changed = false;
        foreach (WindowRuntimeState window in _runtime.Windows)
        {
            if (window.Content is not null || !TryGetOwner(window, out ProcessId pid))
            {
                continue;
            }

            WindowWatch watch = Watch(window.Id, pid);
            if (!TrySample(watch, now, out ProcessCpuTimes times, out TimeSpan grew))
            {
                continue;
            }

            if (!times.IsResponding && grew > TimeSpan.Zero && times.VoluntaryContextSwitches != watch.AcknowledgedSlices)
            {
                // Case 1: the process is "Not responding" and still busy since the previous sample.
                changed |= MarkUnresponsive(watch, now);
            }
            else if (watch.IsUnresponsive && (times.IsResponding || now - watch.LastBusyAt >= Threshold))
            {
                // A short slice completed, or the process stayed idle for a whole threshold: it answers.
                changed |= MarkResponding(watch, times);
            }

            if (watch.IsUnresponsive && !watch.IsDialogOpen && IsDialogEnabled && now >= watch.SnoozedUntil)
            {
                // "Attendre" expired and the window still does not answer: ask again (ADR 0127 §3).
                watch.IsDialogOpen = true;
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Reports how long an input delivered to <paramref name="windowId"/> waited before the browser
    /// could dispatch it (cases 2 and 3 of ADR 0127 §2; a fast input ends the state).
    /// </summary>
    /// <param name="windowId">The window the pointer or keyboard input targeted.</param>
    /// <param name="latency">Time between the input and its dispatch (<c>performance.now() - event.timeStamp</c>).</param>
    public void ReportInput(WindowId windowId, TimeSpan latency)
    {
        WindowRuntimeState? window = _runtime.Windows.FirstOrDefault(candidate => candidate.Id == windowId);
        if (window is null || window.Content is not null || !TryGetOwner(window, out ProcessId pid))
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        WindowWatch watch = Watch(windowId, pid);
        if (!TrySample(watch, now, out ProcessCpuTimes times, out TimeSpan grew))
        {
            return;
        }

        bool waitedOnThisProcess = latency >= Threshold && grew.Ticks * 2 >= Math.Min(latency.Ticks, (now - watch.PreviousSampleAt).Ticks);
        bool longSliceDuringInput = latency >= LongInputDelay && !times.IsResponding && grew >= Threshold;
        bool changed = waitedOnThisProcess || longSliceDuringInput
            ? MarkUnresponsive(watch, now) // Case 2 or case 3.
            : latency < Threshold && watch.IsUnresponsive && MarkResponding(watch, times);
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Handles the close button of a window (ADR 0127 §3): over an unresponsive window it opens the
    /// "not responding" dialog instead of starting the normal close, which the frozen app could not answer.
    /// </summary>
    /// <param name="windowId">The window whose close button was activated.</param>
    /// <returns><see langword="true"/> when the dialog took over; otherwise the caller closes normally.</returns>
    public bool InterceptClose(WindowId windowId)
    {
        if (!_watches.TryGetValue(windowId, out WindowWatch? watch) || !watch.IsUnresponsive)
        {
            return false;
        }

        if (!watch.IsDialogOpen)
        {
            watch.IsDialogOpen = true;
            Changed?.Invoke();
        }

        return true;
    }

    /// <summary>
    /// "Attendre": closes the dialog; it comes back after one threshold if the window still does not
    /// answer (ADR 0127 §3).
    /// </summary>
    /// <param name="windowId">The window whose dialog is dismissed.</param>
    public void Wait(WindowId windowId)
    {
        if (!_watches.TryGetValue(windowId, out WindowWatch? watch) || !watch.IsDialogOpen)
        {
            return;
        }

        watch.IsDialogOpen = false;
        watch.SnoozedUntil = _time.GetUtcNow() + Threshold;
        Changed?.Invoke();
    }

    /// <summary>
    /// "Terminer l'application": asks the window's process to stop with the reason
    /// <see cref="ProcessExitReason.Terminated"/> and a grace of <see cref="TerminateGracePeriod"/>,
    /// past which the process manager kills it (ADR 0069 steps 3 and 4). The window closes when its
    /// process ends, through the shell's usual process-state handling.
    /// </summary>
    /// <param name="windowId">The window whose app is ended.</param>
    /// <param name="cancellationToken">Stops waiting for the end (the stop itself is not withdrawn).</param>
    public async Task TerminateAsync(WindowId windowId, CancellationToken cancellationToken = default)
    {
        if (!_watches.TryGetValue(windowId, out WindowWatch? watch))
        {
            return;
        }

        watch.IsDialogOpen = false;
        Changed?.Invoke();
        try
        {
            await _processes.StopAsync(watch.Pid, TerminateGracePeriod, ProcessExitReason.Terminated, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            // The process ended on its own in the meantime: nothing left to terminate.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.EventsApplied -= OnWindowEvents;
        if (_settings is not null)
        {
            _settings.DocumentChanged -= OnSettingsChanged;
        }
    }

    private bool MarkUnresponsive(WindowWatch watch, DateTimeOffset now)
    {
        watch.LastBusyAt = now;
        if (watch.IsUnresponsive)
        {
            return false;
        }

        watch.IsUnresponsive = true;
        watch.IsDialogOpen = IsDialogEnabled && now >= watch.SnoozedUntil;
        ApplyResponding(watch.WindowId, isResponding: false);
        return true;
    }

    private bool MarkResponding(WindowWatch watch, ProcessCpuTimes times)
    {
        // The accountant's flag sticks until the next short slice (ADR 0067): remember the slices seen
        // now so the same stale flag does not mark the window again.
        watch.AcknowledgedSlices = times.VoluntaryContextSwitches;
        if (!watch.IsUnresponsive)
        {
            return false;
        }

        watch.IsUnresponsive = false;
        watch.IsDialogOpen = false;
        watch.SnoozedUntil = DateTimeOffset.MinValue;
        ApplyResponding(watch.WindowId, isResponding: true);
        return true;
    }

    private void ApplyResponding(WindowId windowId, bool isResponding)
    {
        try
        {
            _runtime.Apply(new SetWindowRespondingCommand(windowId, isResponding));
        }
        catch (KeyNotFoundException)
        {
            // The window closed between the sample and the verdict.
        }
    }

    private bool TrySample(WindowWatch watch, DateTimeOffset now, out ProcessCpuTimes times, out TimeSpan grew)
    {
        grew = TimeSpan.Zero;
        if (!_accountant.IsMeasuring || !_accountant.TryGetProcessTimes(watch.Pid, out times))
        {
            times = default;
            return false;
        }

        if (watch.HasSample)
        {
            grew = times.TotalTime - watch.LastTotal;
            if (grew > TimeSpan.Zero)
            {
                // Still burning CPU: the idle exit rule waits a whole threshold from here.
                watch.LastBusyAt = now;
            }
        }

        watch.PreviousSampleAt = watch.HasSample ? watch.LastSampleAt : now;
        watch.LastSampleAt = now;
        watch.LastTotal = times.TotalTime;
        watch.HasSample = true;
        return true;
    }

    private WindowWatch Watch(WindowId windowId, ProcessId pid)
    {
        if (!_watches.TryGetValue(windowId, out WindowWatch? watch) || watch.Pid != pid)
        {
            watch = new WindowWatch(windowId, pid);
            _watches[windowId] = watch;
        }

        return watch;
    }

    private bool TryGetOwner(WindowRuntimeState window, out ProcessId pid)
    {
        AppInstanceId instance = AppInstanceId.FromGuid(window.OwnerInstanceId.Value);
        foreach (ProcessRecord process in _processes.GetActiveProcesses())
        {
            if (process.AppInstanceId == instance && process.Kind == AppKind.Window)
            {
                pid = process.Pid;
                return true;
            }
        }

        pid = default;
        return false;
    }

    private void OnWindowEvents(IReadOnlyList<WindowEvent> events)
    {
        foreach (WindowEvent windowEvent in events)
        {
            if (windowEvent is WindowClosedEvent closed && _watches.Remove(closed.WindowId, out WindowWatch? watch) && watch.IsDialogOpen)
            {
                Changed?.Invoke();
            }
        }
    }

    private void OnSettingsChanged(object? sender, OsSettingsChangedEventArgs args)
    {
        if (args.Concerns(WindowManagerSettingsDocuments.DocumentId))
        {
            _ = ReloadAsync(CancellationToken.None);
        }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (_settings is null)
        {
            return;
        }

        try
        {
            LayeredConfigSnapshot snapshot = await _settings
                .ReadAsync(WindowManagerSettingsDocuments.Document, cancellationToken)
                .ConfigureAwait(false);
            IsDialogEnabled = WindowManagerSettingsDocuments.IsUnresponsiveDialogEnabled(snapshot);
            foreach (LayeredConfigWarning warning in snapshot.Warnings)
            {
                _logger.LogWarning("{Warning}", warning.ToLogLine());
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "window-manager: window-manager.config could not be read, the dialog stays on");
            IsDialogEnabled = true;
        }
    }

    /// <summary>What the monitor knows of one app window.</summary>
    private sealed class WindowWatch(WindowId windowId, ProcessId pid)
    {
        public WindowId WindowId { get; } = windowId;

        public ProcessId Pid { get; } = pid;

        public bool HasSample { get; set; }

        public TimeSpan LastTotal { get; set; }

        public DateTimeOffset LastSampleAt { get; set; }

        public DateTimeOffset PreviousSampleAt { get; set; }

        public DateTimeOffset LastBusyAt { get; set; }

        /// <summary>The slice count at which a stale "Not responding" flag was already answered (-1: none).</summary>
        public long AcknowledgedSlices { get; set; } = -1;

        public bool IsUnresponsive { get; set; }

        public bool IsDialogOpen { get; set; }

        public DateTimeOffset SnoozedUntil { get; set; } = DateTimeOffset.MinValue;
    }
}
