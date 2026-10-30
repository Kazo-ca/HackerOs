namespace HackerOs.Windowing.Abstractions;

/// <summary>Base contract for an immutable request to mutate window runtime state.</summary>
public abstract record WindowCommand;

/// <summary>Requests registration of a validated initial window snapshot.</summary>
public sealed record CreateWindowCommand(WindowRuntimeState InitialState) : WindowCommand;

/// <summary>Requests focus and front placement for a window.</summary>
public sealed record FocusWindowCommand(WindowId WindowId) : WindowCommand;

/// <summary>Requests a new top-left position in CSS pixels.</summary>
public sealed record MoveWindowCommand(WindowId WindowId, double X, double Y) : WindowCommand;

/// <summary>Requests new position and dimensions for an edge/corner resize.</summary>
public sealed record ResizeWindowCommand(WindowId WindowId, WindowBounds Bounds) : WindowCommand;

/// <summary>
/// Ends an interactive resize (pointer released, keyboard resize validated): the runtime reports one
/// <see cref="WindowResizeCompletedEvent"/>, like the configure notification an X11 client receives
/// once the window manager releases its grab, instead of one per intermediate size.
/// </summary>
public sealed record CompleteWindowResizeCommand(WindowId WindowId) : WindowCommand;

/// <summary>Requests taskbar minimization for a window.</summary>
public sealed record MinimizeWindowCommand(WindowId WindowId) : WindowCommand;

/// <summary>Requests maximization within the current work area.</summary>
public sealed record MaximizeWindowCommand(WindowId WindowId) : WindowCommand;

/// <summary>
/// Requests restoration to the last normal geometry: a maximized or minimized window becomes normal,
/// and a snapped window gets back the geometry it had before it was snapped (ADR 0177).
/// </summary>
public sealed record RestoreWindowCommand(WindowId WindowId) : WindowCommand;

/// <summary>
/// Tiles a window on <paramref name="Bounds"/> (half of the work area) and keeps the geometry it had
/// before in <see cref="WindowRuntimeState.RestoreBounds"/>, so <see cref="RestoreWindowCommand"/>,
/// or dragging its title bar, gives it back (ADR 0177). The window stays
/// <see cref="WindowVisualState.Normal"/> and <see cref="WindowRuntimeState.IsSnapped"/> becomes
/// <see langword="true"/>.
/// </summary>
/// <param name="WindowId">The window to tile.</param>
/// <param name="Bounds">The tile, constrained like any normal geometry.</param>
public sealed record SnapWindowCommand(WindowId WindowId, WindowBounds Bounds) : WindowCommand;

/// <summary>Requests a graceful, cancellable close.</summary>
public sealed record RequestWindowCloseCommand(WindowId WindowId) : WindowCommand;

/// <summary>Cancels a pending graceful close after confirmation is declined.</summary>
public sealed record CancelWindowCloseCommand(WindowId WindowId) : WindowCommand;

/// <summary>Requests unconditional final removal after lifecycle coordination.</summary>
public sealed record ForceWindowCloseCommand(WindowId WindowId) : WindowCommand;

/// <summary>Reports a new desktop work area that may constrain every window of one screen.</summary>
public sealed record ChangeViewportCommand(WindowBounds WorkArea) : WindowCommand
{
    /// <summary>Reports a new work area for screen <paramref name="displayId"/> (ADR 0178 §2).</summary>
    /// <param name="workArea">The screen's new work area.</param>
    /// <param name="displayId">The screen whose work area changed; it must be registered.</param>
    public ChangeViewportCommand(WindowBounds workArea, DisplayId displayId)
        : this(workArea) => DisplayId = displayId;

    /// <summary>Gets the screen whose work area changed; <see cref="DisplayId.Primary"/> by default.</summary>
    public DisplayId DisplayId { get; init; }
}

/// <summary>
/// Registers a new screen of the extended desktop with its work area (ADR 0178 §2). The screen
/// starts empty; windows reach it with <see cref="MoveWindowToDisplayCommand"/>.
/// </summary>
/// <param name="DisplayId">The new screen; it must not be registered yet.</param>
/// <param name="WorkArea">The screen's work area, in its own coordinates.</param>
public sealed record AddDisplayCommand(DisplayId DisplayId, WindowBounds WorkArea) : WindowCommand;

/// <summary>
/// Removes a screen whose surface closed, was lost, or whose monitor was unplugged (ADR 0178 §2):
/// its windows come back to screen 1 and keep their visual state. Screen 1 cannot be removed.
/// </summary>
/// <param name="DisplayId">The screen to remove.</param>
public sealed record RemoveDisplayCommand(DisplayId DisplayId) : WindowCommand;

/// <summary>
/// Moves a window to another screen and keeps its visual state (ADR 0178 §2): a maximized window
/// stays maximized, a window snapped to one half stays on that half, a normal window keeps its
/// relative position, a minimized one stays minimized. Its owner-modal family moves with it.
/// </summary>
/// <param name="WindowId">The window to move.</param>
/// <param name="DisplayId">The target screen; it must be registered.</param>
public sealed record MoveWindowToDisplayCommand(WindowId WindowId, DisplayId DisplayId) : WindowCommand;

/// <summary>
/// Replaces a window's displayed title (ADR 0117 §3), like an X11 client writing
/// <c>_NET_WM_NAME</c>: the runtime cleans it with <see cref="WindowTitles.Sanitize"/> and shows it
/// as given, without adding the app's name.
/// </summary>
/// <param name="WindowId">The window to rename.</param>
/// <param name="Title">The new title; must not be empty once cleaned.</param>
public sealed record SetWindowTitleCommand(WindowId WindowId, string Title) : WindowCommand;

/// <summary>
/// Marks a window as answering its inputs or not (ADR 0127 §2), the way a window manager flags a
/// client that missed its <c>_NET_WM_PING</c>. Issued by the platform's responsiveness monitor,
/// never by the app itself.
/// </summary>
/// <param name="WindowId">The window whose state changes.</param>
/// <param name="IsResponding">Whether the window answers its inputs.</param>
public sealed record SetWindowRespondingCommand(WindowId WindowId, bool IsResponding) : WindowCommand;

/// <summary>
/// Replaces a window's icon (ADR 0203 §4), like an X11 client writing <c>_NET_WM_ICON</c>: the shell
/// issues it when the icon pack changes, so the title bar and the taskbar of open windows follow.
/// </summary>
/// <param name="WindowId">The window whose icon changes.</param>
/// <param name="IconAssetPath">The new image URL, or <see langword="null"/> for the generic window symbol.</param>
public sealed record SetWindowIconCommand(WindowId WindowId, string? IconAssetPath) : WindowCommand;

/// <summary>Base contract for an immutable fact emitted after a window transition.</summary>
public abstract record WindowEvent;

/// <summary>Reports that a window entered the runtime.</summary>
public sealed record WindowCreatedEvent(WindowRuntimeState Window) : WindowEvent;

/// <summary>Reports a focus ownership change.</summary>
public sealed record WindowFocusedEvent(WindowId? PreviousWindowId, WindowId WindowId, int ZOrder) : WindowEvent;

/// <summary>Reports a completed move.</summary>
public sealed record WindowMovedEvent(WindowId WindowId, WindowBounds PreviousBounds, WindowBounds Bounds) : WindowEvent;

/// <summary>Reports a completed resize.</summary>
public sealed record WindowResizedEvent(WindowId WindowId, WindowBounds PreviousBounds, WindowBounds Bounds) : WindowEvent;

/// <summary>Reports that an interactive resize ended with the window at <paramref name="Bounds"/>.</summary>
public sealed record WindowResizeCompletedEvent(WindowId WindowId, WindowBounds Bounds) : WindowEvent;

/// <summary>Reports a visual-state transition.</summary>
public sealed record WindowVisualStateChangedEvent(
    WindowId WindowId,
    WindowVisualState PreviousState,
    WindowVisualState State,
    WindowBounds Bounds,
    WindowBounds? RestoreBounds) : WindowEvent;

/// <summary>Reports that graceful close coordination must begin.</summary>
public sealed record WindowCloseRequestedEvent(WindowId WindowId) : WindowEvent;

/// <summary>Reports that a pending close was cancelled and the window remains active.</summary>
public sealed record WindowCloseCancelledEvent(WindowId WindowId) : WindowEvent;

/// <summary>Reports final removal from the runtime.</summary>
public sealed record WindowClosedEvent(WindowId WindowId, bool WasForced) : WindowEvent;

/// <summary>Reports that a window's displayed title changed (ADR 0117 §3).</summary>
public sealed record WindowTitleChangedEvent(WindowId WindowId, string PreviousTitle, string Title) : WindowEvent;

/// <summary>Reports that a window's icon changed (ADR 0203 §4).</summary>
public sealed record WindowIconChangedEvent(WindowId WindowId, string? PreviousIconAssetPath, string? IconAssetPath) : WindowEvent;

/// <summary>Reports that a window started or stopped answering its inputs (ADR 0127 §2).</summary>
public sealed record WindowRespondingChangedEvent(WindowId WindowId, bool IsResponding) : WindowEvent;

/// <summary>Reports a work-area change and the resulting window snapshots.</summary>
public sealed record WindowViewportChangedEvent(WindowBounds WorkArea, IReadOnlyList<WindowRuntimeState> Windows) : WindowEvent
{
    /// <summary>Reports a work-area change of screen <paramref name="displayId"/> (ADR 0178 §2).</summary>
    /// <param name="workArea">The screen's new work area.</param>
    /// <param name="windows">The snapshots of every window after the change.</param>
    /// <param name="displayId">The screen whose work area changed.</param>
    public WindowViewportChangedEvent(WindowBounds workArea, IReadOnlyList<WindowRuntimeState> windows, DisplayId displayId)
        : this(workArea, windows) => DisplayId = displayId;

    /// <summary>Gets the screen whose work area changed; <see cref="DisplayId.Primary"/> by default.</summary>
    public DisplayId DisplayId { get; init; }
}

/// <summary>Reports that a screen joined the extended desktop (ADR 0178 §2).</summary>
/// <param name="DisplayId">The new screen.</param>
/// <param name="WorkArea">Its work area.</param>
public sealed record DisplayAddedEvent(DisplayId DisplayId, WindowBounds WorkArea) : WindowEvent;

/// <summary>
/// Reports that a screen left the extended desktop (ADR 0178 §2). It follows the
/// <see cref="WindowDisplayChangedEvent"/> of every window that came back to screen 1.
/// </summary>
/// <param name="DisplayId">The removed screen.</param>
public sealed record DisplayRemovedEvent(DisplayId DisplayId) : WindowEvent;

/// <summary>Reports that a window now shows on another screen (ADR 0178 §2).</summary>
/// <param name="WindowId">The window that moved.</param>
/// <param name="PreviousDisplayId">The screen it left.</param>
/// <param name="DisplayId">The screen it shows on now.</param>
/// <param name="Bounds">Its bounds in the new screen's coordinates.</param>
/// <param name="RestoreBounds">Its restore geometry in the new screen's coordinates, if any.</param>
public sealed record WindowDisplayChangedEvent(
    WindowId WindowId,
    DisplayId PreviousDisplayId,
    DisplayId DisplayId,
    WindowBounds Bounds,
    WindowBounds? RestoreBounds) : WindowEvent;
