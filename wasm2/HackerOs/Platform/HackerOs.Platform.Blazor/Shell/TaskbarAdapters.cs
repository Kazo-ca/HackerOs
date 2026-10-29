using System.Globalization;
using HackerOs.Platform.Blazor.Windows;
using HackerOs.Platform.Core.Globalization;
using HackerOs.Platform.Core.Shell;
using HackerOs.Simulation.Abstractions.Time;
using HackerOs.Taskbar.Blazor;
using HackerOs.Windowing.Core;
using HackerOs.Windowing.Abstractions;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>Projects the authoritative window runtime into the taskbar's generic window contract.</summary>
public sealed class TaskbarWindowSourceAdapter : ITaskbarWindowSource, IDisposable
{
    private readonly WindowRuntime _runtime;
    private readonly Func<WindowRuntimeState, bool>? _lists;

    /// <summary>Creates an adapter over the desktop window runtime.</summary>
    public TaskbarWindowSourceAdapter(WindowRuntime runtime)
        : this(runtime, lists: null)
    {
    }

    /// <summary>Creates an adapter that lists only the windows <paramref name="lists"/> keeps (the taskbar of one screen, ADR 0179 §1).</summary>
    /// <param name="runtime">The desktop window runtime.</param>
    /// <param name="lists">Whether a window shows on this taskbar; <see langword="null"/> lists every window.</param>
    internal TaskbarWindowSourceAdapter(WindowRuntime runtime, Func<WindowRuntimeState, bool>? lists)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _lists = lists;
        _runtime.StateChanged += OnRuntimeStateChanged;
    }

    /// <inheritdoc />
    public IReadOnlyList<TaskbarWindowItem> Windows => [.. _runtime.Windows.Where(window => _lists?.Invoke(window) ?? true).Select(ToItem)];

    /// <summary>Tells the taskbar to read the list again, after what <c>lists</c> keeps changed.</summary>
    internal void Refresh() => Changed?.Invoke();

    /// <inheritdoc />
    public event Action? Changed;

    private static TaskbarWindowItem ToItem(WindowRuntimeState window) => new(
        window.Id,
        window.Title,
        window.IconAssetPath,
        window.IsFocused,
        window.VisualState == WindowVisualState.Minimized);

    private void OnRuntimeStateChanged() => Changed?.Invoke();

    /// <inheritdoc />
    public void Dispose() => _runtime.StateChanged -= OnRuntimeStateChanged;
}

/// <summary>Which windows the taskbar of a screen lists (ADR 0179 §1).</summary>
internal static class TaskbarScreenFilter
{
    /// <summary>
    /// The taskbar of a secondary screen lists the windows of its screen; the taskbar of screen 1 lists
    /// every window (<see cref="PrimaryTaskbarMode.All"/>) or only those of screen 1 (<see cref="PrimaryTaskbarMode.Own"/>).
    /// </summary>
    /// <param name="taskbar">The screen of the taskbar.</param>
    /// <param name="primaryTaskbar">The <c>primary-taskbar</c> setting.</param>
    /// <param name="window">The screen of the window.</param>
    public static bool Lists(DisplayId taskbar, PrimaryTaskbarMode primaryTaskbar, DisplayId window) =>
        taskbar.IsPrimary
            ? primaryTaskbar == PrimaryTaskbarMode.All || window.IsPrimary
            : window == taskbar;
}

/// <summary>Dispatches taskbar-initiated window commands onto the authoritative window runtime.</summary>
public sealed class TaskbarCommandDispatcherAdapter(
    WindowRuntime runtime,
    WindowCloseCoordinator closeCoordinator,
    WindowCloseGuardRegistry closeGuards) : ITaskbarCommandDispatcher
{
    /// <inheritdoc />
    public void Activate(WindowId id) => runtime.Apply(new FocusWindowCommand(id));

    /// <inheritdoc />
    public void Minimize(WindowId id) => runtime.Apply(new MinimizeWindowCommand(id));

    /// <inheritdoc />
    public void Restore(WindowId id) => runtime.Apply(new RestoreWindowCommand(id));

    /// <inheritdoc />
    public void Close(WindowId id) => _ = CloseAsync(id);

    private async Task CloseAsync(WindowId id)
    {
        WindowRuntimeState? window = runtime.Windows.FirstOrDefault(w => w.Id == id);
        if (window?.OnRequestClose is { } onRequestClose)
        {
            await onRequestClose();
            return;
        }

        await closeCoordinator.CloseAsync(
            id,
            cancellationToken => closeGuards.ConfirmCloseAsync(id, cancellationToken));
    }
}

/// <summary>
/// Owns the desktop shell's application launcher open/closed state so both the shell's own
/// launcher panel and the taskbar's launcher trigger stay in sync regardless of which one
/// changed it.
/// </summary>
public sealed class TaskbarLauncherAdapter : ITaskbarLauncher
{
    /// <inheritdoc />
    public bool IsOpen { get; private set; }

    /// <summary>Gets the screen the launcher shows on while open: the screen of the taskbar that opened it (ADR 0179 §1).</summary>
    internal DisplayId Display { get; private set; }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public void Toggle() => ToggleOn(IsOpen ? Display : DisplayId.Primary);

    /// <summary>Opens the launcher.</summary>
    public void Open() => SetOpen(true, IsOpen ? Display : DisplayId.Primary);

    /// <summary>Closes the launcher.</summary>
    public void Close() => SetOpen(false, Display);

    /// <summary>
    /// The Start button of <paramref name="display"/>'s taskbar: closes the launcher open on that screen,
    /// otherwise opens it there (moving it from another screen).
    /// </summary>
    /// <param name="display">The screen of the taskbar clicked.</param>
    internal void ToggleOn(DisplayId display) => SetOpen(!(IsOpen && Display == display), display);

    /// <summary>The launcher as the taskbar of <paramref name="display"/> sees it: open only when it shows on that screen.</summary>
    /// <param name="display">The screen of the taskbar.</param>
    internal ITaskbarLauncher For(DisplayId display) => new ScreenView(this, display);

    private void SetOpen(bool value, DisplayId display)
    {
        if (IsOpen == value && (!value || Display == display))
        {
            return;
        }

        IsOpen = value;
        Display = value ? display : Display;
        Changed?.Invoke();
    }

    private sealed class ScreenView(TaskbarLauncherAdapter launcher, DisplayId display) : ITaskbarLauncher
    {
        public bool IsOpen => launcher.IsOpen && launcher.Display == display;

        public void Toggle() => launcher.ToggleOn(display);

        public event Action? Changed
        {
            add => launcher.Changed += value;
            remove => launcher.Changed -= value;
        }
    }
}

/// <summary>
/// Owns the clock panel's open/closed state. Unlike the notification bell it replaces, this state
/// now actually gates the panel's render in <c>DesktopShell</c> — the panel's own content (unread
/// count included) is computed inside <c>ClockPanel</c>, not on this trigger contract, since the
/// taskbar package must never need to know what's inside <c>ClockPanelContent</c>.
/// </summary>
public sealed class TaskbarClockPanelSourceAdapter : ITaskbarClockPanelSource
{
    /// <inheritdoc />
    public bool IsOpen { get; private set; }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public void Toggle() => SetOpen(!IsOpen);

    /// <summary>Closes the clock panel.</summary>
    public void Close() => SetOpen(false);

    private void SetOpen(bool value)
    {
        if (IsOpen == value)
        {
            return;
        }

        IsOpen = value;
        Changed?.Invoke();
    }
}

/// <summary>Ticks the taskbar clock once per simulated second from the session's clock.</summary>
public sealed class TaskbarStatusSourceAdapter : ITaskbarStatusSource, IDisposable
{
    private readonly ISimulationClock _clock;
    private readonly SessionLocaleService? _locale;
    private readonly SessionTimeZoneService? _timeZones;
    private IDisposable? _subscription;

    /// <summary>Creates a status source over the session's simulation clock.</summary>
    /// <param name="clock">The session's simulation clock.</param>
    /// <param name="locale">
    /// The session language, whose <c>LC_TIME</c> culture formats the clock (ADR 0104);
    /// <see langword="null"/> keeps the taskbar's invariant <c>HH:mm:ss</c>.
    /// </param>
    public TaskbarStatusSourceAdapter(ISimulationClock clock, SessionLocaleService? locale = null)
        : this(clock, locale, timeZones: null)
    {
    }

    /// <summary>Creates a status source that shows the clock in the session's time zone (ADR 0193).</summary>
    /// <param name="clock">The session's simulation clock.</param>
    /// <param name="locale">The session language, whose <c>LC_TIME</c> culture formats the clock.</param>
    /// <param name="timeZones">The session's time zone, followed live; <see langword="null"/> keeps UTC.</param>
    public TaskbarStatusSourceAdapter(ISimulationClock clock, SessionLocaleService? locale, SessionTimeZoneService? timeZones)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _locale = locale;
        _timeZones = timeZones;
        if (_timeZones is not null)
        {
            _timeZones.Changed += OnTimeZoneChanged;
        }

        ScheduleNextTick();
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow => _clock.UtcNow;

    /// <inheritdoc />
    public CultureInfo FormatCulture => _locale?.FormatCulture ?? CultureInfo.InvariantCulture;

    /// <inheritdoc />
    public TimeZoneInfo TimeZone => _timeZones?.TimeZone ?? TimeZoneInfo.Utc;

    /// <inheritdoc />
    public event Action? Changed;

    private void ScheduleNextTick()
    {
        _subscription?.Dispose();
        _subscription = _clock.Schedule(TimeSpan.FromSeconds(1), () =>
        {
            Changed?.Invoke();
            ScheduleNextTick();
        });
    }

    // A new zone shows at once, not at the next tick.
    private void OnTimeZoneChanged() => Changed?.Invoke();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_timeZones is not null)
        {
            _timeZones.Changed -= OnTimeZoneChanged;
        }

        _subscription?.Dispose();
    }
}

/// <summary>Routes the taskbar's logout command into the desktop shell's own logout flow.</summary>
public sealed class TaskbarSessionCommandsAdapter(Action requestLogout) : ITaskbarSessionCommands
{
    /// <inheritdoc />
    public void RequestLogout() => requestLogout();
}
