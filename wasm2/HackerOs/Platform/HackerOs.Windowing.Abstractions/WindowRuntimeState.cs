using Microsoft.AspNetCore.Components;

namespace HackerOs.Windowing.Abstractions;

/// <summary>Describes the visible layout state of a window.</summary>
public enum WindowVisualState
{
    /// <summary>The window uses its current freeform geometry.</summary>
    Normal,
    /// <summary>The window is represented by the taskbar and hidden from the desktop.</summary>
    Minimized,
    /// <summary>The window fills the current desktop work area.</summary>
    Maximized,
}

/// <summary>Describes whether a window blocks interaction with an owner.</summary>
public enum WindowModality
{
    /// <summary>The window does not block another window.</summary>
    Modeless,
    /// <summary>The window blocks its owner until it closes.</summary>
    OwnerModal,
}

/// <summary>Contains all renderer-independent authoritative state for one window.</summary>
public sealed record WindowRuntimeState
{
    /// <summary>Creates a validated window state snapshot.</summary>
    public WindowRuntimeState(
        WindowId id,
        string appId,
        WindowOwnerId ownerInstanceId,
        string title,
        string? iconAssetPath,
        WindowBounds bounds,
        WindowBounds? restoreBounds,
        int zOrder,
        WindowVisualState visualState,
        WindowConstraints constraints,
        WindowModality modality = WindowModality.Modeless,
        WindowId? ownerId = null,
        bool isFocused = false,
        RenderFragment? content = null,
        Func<Task>? onRequestClose = null,
        bool supportsBack = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        if (id == default)
        {
            throw new ArgumentException("Window ID must be initialized.", nameof(id));
        }

        if (zOrder < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(zOrder), "Z-order cannot be negative.");
        }

        if (modality == WindowModality.OwnerModal && ownerId is null)
        {
            throw new ArgumentException("An owner-modal window requires an owner.", nameof(ownerId));
        }

        if (ownerId == id)
        {
            throw new ArgumentException("A window cannot own itself.", nameof(ownerId));
        }

        Id = id;
        AppId = appId;
        OwnerInstanceId = ownerInstanceId;
        Title = title;
        IconAssetPath = iconAssetPath;
        Bounds = bounds;
        RestoreBounds = restoreBounds;
        ZOrder = zOrder;
        VisualState = visualState;
        Constraints = constraints ?? throw new ArgumentNullException(nameof(constraints));
        Modality = modality;
        OwnerId = ownerId;
        IsFocused = isFocused;
        Content = content;
        OnRequestClose = onRequestClose;
        SupportsBack = supportsBack;
    }

    /// <summary>Gets the window identity.</summary>
    public WindowId Id { get; }

    /// <summary>Gets the immutable application identity.</summary>
    public string AppId { get; }

    /// <summary>Gets the opaque owning app instance identity.</summary>
    public WindowOwnerId OwnerInstanceId { get; }

    /// <summary>Gets the displayed title.</summary>
    public string Title { get; }

    /// <summary>Gets the optional package-local icon asset path.</summary>
    public string? IconAssetPath { get; }

    /// <summary>Gets the current normal-state geometry.</summary>
    public WindowBounds Bounds { get; }

    /// <summary>
    /// Gets geometry captured before maximizing, or before snapping when <see cref="IsSnapped"/> is
    /// <see langword="true"/> (ADR 0177).
    /// </summary>
    public WindowBounds? RestoreBounds { get; }

    /// <summary>Gets the deterministic stacking order.</summary>
    public int ZOrder { get; }

    /// <summary>Gets the visible layout state.</summary>
    public WindowVisualState VisualState { get; }

    /// <summary>Gets resize and dimension constraints.</summary>
    public WindowConstraints Constraints { get; }

    /// <summary>Gets the blocking relationship type.</summary>
    public WindowModality Modality { get; }

    /// <summary>Gets the optional owning window.</summary>
    public WindowId? OwnerId { get; }

    /// <summary>Gets whether this window currently owns keyboard focus.</summary>
    public bool IsFocused { get; }

    /// <summary>
    /// Gets the content rendered inside the window chrome in place of the running app instance.
    /// Set by whoever creates a non-app window (e.g. a dialog projection) so the desktop shell
    /// never needs to know that window's type.
    /// </summary>
    public RenderFragment? Content { get; }

    /// <summary>
    /// Gets the delegate invoked instead of the normal close-guard flow when the user requests
    /// this window be closed. Lets a dialog window route "close" to its own cancel/dismiss logic
    /// without the desktop shell special-casing it.
    /// </summary>
    public Func<Task>? OnRequestClose { get; }

    /// <summary>
    /// Gets whether the shell should present Back navigation chrome for this window, per
    /// doc/en/wasm/dev/old/mobile-interface-platform-plan.md §8 (<c>MOB-012</c>/<c>MOB-013</c>). Set once at
    /// window-creation time from the launching app's manifest <c>supportsBack</c> declaration;
    /// does not track the app's live <c>IAppBackHandler.CanNavigateBack</c> state.
    /// </summary>
    public bool SupportsBack { get; }

    /// <summary>
    /// Gets whether the window's app answers its inputs (ADR 0127 §2). <see langword="false"/> while
    /// the platform judges the window unresponsive: the chrome then adds a "(Not responding)" suffix
    /// to the displayed title and greys the content, like a hung window on Windows or a
    /// <c>_NET_WM_PING</c> timeout on X11. <see cref="Title"/> itself never carries the suffix.
    /// </summary>
    public bool IsResponding { get; init; } = true;

    /// <summary>
    /// Gets the screen that shows the window (ADR 0178 §2). <see cref="Bounds"/> and
    /// <see cref="RestoreBounds"/> are in that screen's coordinates. Defaults to
    /// <see cref="DisplayId.Primary"/>; only <see cref="MoveWindowToDisplayCommand"/> and
    /// <see cref="RemoveDisplayCommand"/> change it once the window exists.
    /// </summary>
    public DisplayId DisplayId { get; init; }

    /// <summary>
    /// Gets whether the window is tiled on half of the work area by a snap (ADR 0177). A snapped
    /// window stays <see cref="WindowVisualState.Normal"/> and keeps its geometry from before the snap
    /// in <see cref="RestoreBounds"/>: restoring it, or dragging its title bar, gives that geometry back.
    /// </summary>
    public bool IsSnapped { get; init; }
}
