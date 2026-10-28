using HackerOs.Windowing.Abstractions;

namespace HackerOs.Windowing.Core;

/// <summary>Owns authoritative window snapshots and applies deterministic transitions.</summary>
public sealed class WindowRuntime
{
    private readonly Dictionary<WindowId, WindowRuntimeState> _windows = [];
    private readonly HashSet<WindowId> _pendingClose = [];
    private readonly Dictionary<DisplayId, WindowBounds> _workAreas = [];
    private int _nextZOrder;

    /// <summary>Event raised whenever window state or active windows change.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Event raised with the ordered facts of every command that changed something, before
    /// <see cref="StateChanged"/>. Observers that need the transitions themselves (focus moving from
    /// one window to another, the end of a resize) listen here instead of diffing snapshots.
    /// </summary>
    public event Action<IReadOnlyList<WindowEvent>>? EventsApplied;

    /// <summary>Gets the current work area of screen 1 (<see cref="DisplayId.Primary"/>).</summary>
    public WindowBounds WorkArea => _workAreas[DisplayId.Primary];

    /// <summary>Gets the registered screens, screen 1 first, then by number (ADR 0178 §2).</summary>
    public IReadOnlyList<DisplayId> Displays => [.. _workAreas.Keys.OrderBy(display => display.Number)];

    /// <summary>
    /// Gets the screen a new screen should take: the smallest free number (ADR 0178 §5).
    /// </summary>
    public DisplayId NextDisplayId
    {
        get
        {
            int number = 1;
            while (_workAreas.ContainsKey(DisplayId.FromNumber(number)))
            {
                number++;
            }

            return DisplayId.FromNumber(number);
        }
    }

    /// <summary>Creates a runtime whose screen 1 has the supplied work area.</summary>
    public WindowRuntime(WindowBounds workArea) => _workAreas[DisplayId.Primary] = workArea;

    /// <summary>Gets the work area of a registered screen.</summary>
    /// <param name="displayId">The screen.</param>
    /// <exception cref="KeyNotFoundException">The screen is not registered.</exception>
    public WindowBounds GetWorkArea(DisplayId displayId) => _workAreas.TryGetValue(displayId, out WindowBounds area)
        ? area
        : throw new KeyNotFoundException($"Screen '{displayId}' is not registered.");

    /// <summary>Gets stable snapshots ordered from back to front.</summary>
    public IReadOnlyList<WindowRuntimeState> Windows =>
        [.. _windows.Values.OrderBy(window => window.ZOrder).ThenBy(window => window.Id.Value)];

    /// <summary>Gets whether an owner is blocked by one of its active modal windows.</summary>
    public bool IsInteractionBlocked(WindowId id) => _windows.Values.Any(window =>
        window.Modality == WindowModality.OwnerModal
        && window.OwnerId == id
        && window.VisualState != WindowVisualState.Minimized);

    /// <summary>Applies one command atomically and returns ordered facts describing the change.</summary>
    public IReadOnlyList<WindowEvent> Apply(WindowCommand command)
    {
        IReadOnlyList<WindowEvent> events = command switch
        {
            CreateWindowCommand create => Create(create.InitialState),
            FocusWindowCommand focus => Focus(focus.WindowId),
            MoveWindowCommand move => Move(move),
            ResizeWindowCommand resize => Resize(resize),
            CompleteWindowResizeCommand complete => CompleteResize(complete.WindowId),
            MinimizeWindowCommand minimize => ChangeVisualState(minimize.WindowId, WindowVisualState.Minimized),
            MaximizeWindowCommand maximize => ChangeVisualState(maximize.WindowId, WindowVisualState.Maximized),
            RestoreWindowCommand restore => ChangeVisualState(restore.WindowId, WindowVisualState.Normal),
            RequestWindowCloseCommand close => RequestClose(close.WindowId),
            CancelWindowCloseCommand close => CancelClose(close.WindowId),
            ForceWindowCloseCommand close => ForceClose(close.WindowId),
            ChangeViewportCommand viewport => ChangeViewport(viewport.DisplayId, viewport.WorkArea),
            AddDisplayCommand add => AddDisplay(add.DisplayId, add.WorkArea),
            RemoveDisplayCommand remove => RemoveDisplay(remove.DisplayId),
            MoveWindowToDisplayCommand move => MoveToDisplay(move.WindowId, move.DisplayId),
            SetWindowTitleCommand title => ChangeTitle(title.WindowId, title.Title),
            SetWindowRespondingCommand responding => ChangeResponding(responding.WindowId, responding.IsResponding),
            null => throw new ArgumentNullException(nameof(command)),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown window command."),
        };

        if (events.Count > 0)
        {
            EventsApplied?.Invoke(events);
            StateChanged?.Invoke();
        }

        return events;
    }

    /// <summary>
    /// Replaces the displayed title of <paramref name="windowId"/> (ADR 0117 §3). The title is cleaned
    /// with <see cref="WindowTitles.Sanitize"/>; an unchanged title produces no event.
    /// </summary>
    /// <param name="windowId">The window to rename.</param>
    /// <param name="title">The new title; must not be empty once cleaned.</param>
    /// <returns>A <see cref="WindowTitleChangedEvent"/>, or nothing when the title did not change.</returns>
    public IReadOnlyList<WindowEvent> SetTitle(WindowId windowId, string title) =>
        Apply(new SetWindowTitleCommand(windowId, title));

    private IReadOnlyList<WindowEvent> Create(WindowRuntimeState initialState)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        if (_windows.ContainsKey(initialState.Id))
        {
            throw new InvalidOperationException($"Window '{initialState.Id}' is already registered.");
        }

        if (initialState.OwnerId is WindowId ownerId && !_windows.ContainsKey(ownerId))
        {
            throw new InvalidOperationException($"Owner window '{ownerId}' is not registered.");
        }

        // A modal window shows on its owner's screen (ADR 0178 §2); any other window on the screen it names.
        DisplayId displayId = initialState is { Modality: WindowModality.OwnerModal, OwnerId: WindowId modalOwner }
            ? _windows[modalOwner].DisplayId
            : initialState.DisplayId;
        WindowBounds area = GetWorkArea(displayId);

        WindowId? previousFocus = _windows.Values.SingleOrDefault(window => window.IsFocused)?.Id;
        ClearFocus();
        WindowRuntimeState created = Copy(
            initialState,
            bounds: Constrain(initialState.Bounds, initialState.Constraints, area),
            zOrder: NextZOrder(),
            isFocused: true,
            displayId: displayId);
        _windows.Add(created.Id, created);
        return [new WindowCreatedEvent(created), new WindowFocusedEvent(previousFocus, created.Id, created.ZOrder)];
    }

    private IReadOnlyList<WindowEvent> Focus(WindowId id)
    {
        WindowRuntimeState target = Get(id);
        if (IsInteractionBlocked(id))
        {
            throw new InvalidOperationException("A window blocked by an owner-modal child cannot receive focus.");
        }
        if (target.VisualState == WindowVisualState.Minimized)
        {
            throw new InvalidOperationException("A minimized window must be restored before it can receive focus.");
        }

        WindowId? previousFocus = _windows.Values.SingleOrDefault(window => window.IsFocused)?.Id;
        if (previousFocus == id)
        {
            return [];
        }

        ClearFocus();
        target = Copy(target, zOrder: NextZOrder(), isFocused: true);
        _windows[id] = target;
        return [new WindowFocusedEvent(previousFocus, id, target.ZOrder)];
    }

    private IReadOnlyList<WindowEvent> Move(MoveWindowCommand command)
    {
        WindowRuntimeState current = RequireNormal(command.WindowId);
        if (!current.Constraints.IsMovable)
        {
            throw new InvalidOperationException("This window is not movable.");
        }

        WindowBounds bounds = Constrain(
            new WindowBounds(command.X, command.Y, current.Bounds.Width, current.Bounds.Height),
            current.Constraints,
            AreaOf(current));
        _windows[current.Id] = Copy(current, bounds: bounds);
        return [new WindowMovedEvent(current.Id, current.Bounds, bounds)];
    }

    private IReadOnlyList<WindowEvent> Resize(ResizeWindowCommand command)
    {
        WindowRuntimeState current = RequireNormal(command.WindowId);
        if (!current.Constraints.IsResizable)
        {
            throw new InvalidOperationException("This window is not resizable.");
        }

        WindowBounds bounds = Constrain(command.Bounds, current.Constraints, AreaOf(current));
        _windows[current.Id] = Copy(current, bounds: bounds);
        return [new WindowResizedEvent(current.Id, current.Bounds, bounds)];
    }

    private IReadOnlyList<WindowEvent> CompleteResize(WindowId id)
    {
        WindowRuntimeState current = Get(id);
        return [new WindowResizeCompletedEvent(id, current.Bounds)];
    }

    private IReadOnlyList<WindowEvent> ChangeVisualState(WindowId id, WindowVisualState requestedState)
    {
        WindowRuntimeState current = Get(id);
        if (current.VisualState == requestedState)
        {
            return [];
        }

        WindowBounds bounds = current.Bounds;
        WindowBounds? restoreBounds = current.RestoreBounds;
        if (requestedState == WindowVisualState.Maximized)
        {
            restoreBounds = current.VisualState == WindowVisualState.Normal ? current.Bounds : restoreBounds;
            bounds = AreaOf(current);
        }
        else if (requestedState == WindowVisualState.Normal && restoreBounds is not null)
        {
            bounds = restoreBounds.Value;
            restoreBounds = null;
        }

        bool retainFocus = requestedState != WindowVisualState.Minimized && current.IsFocused;
        WindowRuntimeState changed = Copy(
            current,
            bounds: bounds,
            restoreBounds: restoreBounds,
            replaceRestoreBounds: true,
            visualState: requestedState,
            isFocused: retainFocus);
        _windows[id] = changed;

        List<WindowEvent> events = [new WindowVisualStateChangedEvent(id, current.VisualState, requestedState, bounds, restoreBounds)];
        if (requestedState == WindowVisualState.Minimized && current.IsFocused)
        {
            events.AddRange(FocusTopVisible(id));
        }
        else if (requestedState != WindowVisualState.Minimized && !retainFocus)
        {
            // Minimizing a background window leaves the focus where it is.
            events.AddRange(Focus(id));
        }

        return events;
    }

    private IReadOnlyList<WindowEvent> RequestClose(WindowId id)
    {
        if (!_windows.ContainsKey(id))
        {
            return [];
        }

        return _pendingClose.Add(id) ? [new WindowCloseRequestedEvent(id)] : [];
    }

    private IReadOnlyList<WindowEvent> CancelClose(WindowId id)
    {
        if (!_windows.ContainsKey(id))
        {
            return [];
        }

        return _pendingClose.Remove(id) ? [new WindowCloseCancelledEvent(id)] : [];
    }

    private IReadOnlyList<WindowEvent> ForceClose(WindowId id)
    {
        if (!_windows.TryGetValue(id, out WindowRuntimeState? current))
        {
            return [];
        }

        _windows.Remove(id);
        bool wasRequested = _pendingClose.Remove(id);
        List<WindowEvent> events = [new WindowClosedEvent(id, !wasRequested)];
        if (current.IsFocused && current.OwnerId is WindowId ownerId && _windows.ContainsKey(ownerId))
        {
            events.AddRange(Focus(ownerId));
        }
        else if (current.IsFocused)
        {
            events.AddRange(FocusTopVisible(id));
        }

        return events;
    }

    private IReadOnlyList<WindowEvent> ChangeViewport(DisplayId displayId, WindowBounds workArea)
    {
        _ = GetWorkArea(displayId);
        _workAreas[displayId] = workArea;
        foreach (WindowRuntimeState window in _windows.Values.Where(window => window.DisplayId == displayId).ToArray())
        {
            WindowBounds bounds = window.VisualState == WindowVisualState.Maximized
                ? workArea
                : Constrain(window.Bounds, window.Constraints, workArea);
            WindowBounds? restoreBounds = window.RestoreBounds is WindowBounds restore
                ? Constrain(restore, window.Constraints, workArea)
                : null;
            _windows[window.Id] = Copy(
                window,
                bounds: bounds,
                restoreBounds: restoreBounds,
                replaceRestoreBounds: true);
        }

        return [new WindowViewportChangedEvent(workArea, Windows, displayId)];
    }

    private IReadOnlyList<WindowEvent> AddDisplay(DisplayId displayId, WindowBounds workArea)
    {
        if (!_workAreas.TryAdd(displayId, workArea))
        {
            throw new InvalidOperationException($"Screen '{displayId}' is already registered.");
        }

        return [new DisplayAddedEvent(displayId, workArea)];
    }

    /// <summary>
    /// Brings every window of a removed screen back to screen 1 in its current state (ADR 0178 §2),
    /// like the content of a closed display surface returning under its anchor. Focus and stacking
    /// order do not change.
    /// </summary>
    private IReadOnlyList<WindowEvent> RemoveDisplay(DisplayId displayId)
    {
        if (displayId.IsPrimary)
        {
            throw new InvalidOperationException("Screen 1 cannot be removed.");
        }

        if (!_workAreas.ContainsKey(displayId))
        {
            return [];
        }

        List<WindowEvent> events = [];
        foreach (WindowRuntimeState window in Windows.Where(window => window.DisplayId == displayId))
        {
            events.Add(Relocate(window, DisplayId.Primary));
        }

        _workAreas.Remove(displayId);
        events.Add(new DisplayRemovedEvent(displayId));
        return events;
    }

    /// <summary>
    /// Moves a window and its owner-modal family to another screen (ADR 0178 §2): moving a modal
    /// window moves its owner, and moving an owner moves its modal windows. Focus and stacking order
    /// do not change.
    /// </summary>
    private IReadOnlyList<WindowEvent> MoveToDisplay(WindowId id, DisplayId displayId)
    {
        _ = Get(id);
        _ = GetWorkArea(displayId);
        List<WindowEvent> events = [];
        foreach (WindowRuntimeState window in ModalFamily(id).Where(window => window.DisplayId != displayId))
        {
            events.Add(Relocate(window, displayId));
        }

        return events;
    }

    /// <summary>
    /// Places <paramref name="window"/> on <paramref name="target"/> in the same visual state. A
    /// window filling its screen (maximized, or minimized from maximized) fills the target; a normal
    /// window snapped to one half takes the same half; any other geometry keeps its position relative
    /// to the screen and is kept inside the target work area.
    /// </summary>
    private WindowDisplayChangedEvent Relocate(WindowRuntimeState window, DisplayId target)
    {
        WindowBounds source = AreaOf(window);
        WindowBounds area = GetWorkArea(target);
        WindowBounds bounds;
        if (window.VisualState == WindowVisualState.Maximized || window.Bounds == source)
        {
            bounds = area;
        }
        else if (SnappedSide(window.Bounds, source) is bool left)
        {
            bounds = Constrain(HalfOf(area, left), window.Constraints, area);
        }
        else
        {
            bounds = Constrain(MapRelative(window.Bounds, source, area), window.Constraints, area);
        }

        WindowBounds? restoreBounds = window.RestoreBounds is WindowBounds restore
            ? Constrain(MapRelative(restore, source, area), window.Constraints, area)
            : null;
        _windows[window.Id] = Copy(
            window,
            bounds: bounds,
            restoreBounds: restoreBounds,
            replaceRestoreBounds: true,
            displayId: target);
        return new WindowDisplayChangedEvent(window.Id, window.DisplayId, target, bounds, restoreBounds);
    }

    /// <summary>Gets the windows linked to <paramref name="id"/> by owner-modal relations, back to front.</summary>
    private IReadOnlyList<WindowRuntimeState> ModalFamily(WindowId id)
    {
        WindowRuntimeState root = Get(id);
        while (root is { Modality: WindowModality.OwnerModal, OwnerId: WindowId ownerId }
               && _windows.TryGetValue(ownerId, out WindowRuntimeState? owner))
        {
            root = owner;
        }

        HashSet<WindowId> family = [root.Id];
        Queue<WindowId> pending = new([root.Id]);
        while (pending.TryDequeue(out WindowId current))
        {
            foreach (WindowRuntimeState child in _windows.Values.Where(window =>
                         window.Modality == WindowModality.OwnerModal && window.OwnerId == current))
            {
                if (family.Add(child.Id))
                {
                    pending.Enqueue(child.Id);
                }
            }
        }

        return [.. Windows.Where(window => family.Contains(window.Id))];
    }

    /// <summary>Keeps the offset from the screen's top-left corner in proportion to the screen size.</summary>
    private static WindowBounds MapRelative(WindowBounds bounds, WindowBounds source, WindowBounds target) => new(
        target.X + ((bounds.X - source.X) * target.Width / source.Width),
        target.Y + ((bounds.Y - source.Y) * target.Height / source.Height),
        bounds.Width,
        bounds.Height);

    /// <summary>The left or right half-tile of <paramref name="area"/>, as keyboard snapping draws it.</summary>
    private static WindowBounds HalfOf(WindowBounds area, bool left)
    {
        double width = Math.Floor(area.Width / 2);
        return new WindowBounds(left ? area.X : area.X + area.Width - width, area.Y, width, area.Height);
    }

    /// <summary>
    /// <see langword="true"/> for the left half-tile of <paramref name="area"/>, <see langword="false"/>
    /// for the right one, <see langword="null"/> for any other geometry.
    /// </summary>
    private static bool? SnappedSide(WindowBounds bounds, WindowBounds area) =>
        bounds == HalfOf(area, left: true) ? true
        : bounds == HalfOf(area, left: false) ? false
        : null;

    private IReadOnlyList<WindowEvent> ChangeTitle(WindowId id, string requestedTitle)
    {
        string title = WindowTitles.Sanitize(requestedTitle);
        if (title.Length == 0)
        {
            throw new ArgumentException("A window title cannot be empty.", nameof(requestedTitle));
        }

        WindowRuntimeState current = Get(id);
        if (string.Equals(current.Title, title, StringComparison.Ordinal))
        {
            return [];
        }

        _windows[id] = Copy(current, title: title);
        return [new WindowTitleChangedEvent(id, current.Title, title)];
    }

    private IReadOnlyList<WindowEvent> ChangeResponding(WindowId id, bool isResponding)
    {
        WindowRuntimeState current = Get(id);
        if (current.IsResponding == isResponding)
        {
            return [];
        }

        // The flag is display state only (ADR 0127 §3): it never touches Title, so no
        // WindowTitleChangedEvent is raised for the "(Not responding)" suffix.
        _windows[id] = Copy(current) with { IsResponding = isResponding };
        return [new WindowRespondingChangedEvent(id, isResponding)];
    }

    private IReadOnlyList<WindowEvent> FocusTopVisible(WindowId excludedId)
    {
        WindowRuntimeState? next = _windows.Values
            .Where(window => window.Id != excludedId && window.VisualState != WindowVisualState.Minimized)
            .OrderByDescending(window => window.ZOrder)
            .FirstOrDefault();
        return next is null ? [] : Focus(next.Id);
    }

    private WindowRuntimeState RequireNormal(WindowId id)
    {
        WindowRuntimeState window = Get(id);
        return window.VisualState == WindowVisualState.Normal
            ? window
            : throw new InvalidOperationException("Move and resize commands require a normal window.");
    }

    private WindowBounds AreaOf(WindowRuntimeState window) => _workAreas[window.DisplayId];

    private WindowRuntimeState Get(WindowId id) => _windows.TryGetValue(id, out WindowRuntimeState? window)
        ? window
        : throw new KeyNotFoundException($"Window '{id}' is not registered.");

    private void ClearFocus()
    {
        foreach (WindowRuntimeState focused in _windows.Values.Where(window => window.IsFocused).ToArray())
        {
            _windows[focused.Id] = Copy(focused, isFocused: false);
        }
    }

    private int NextZOrder() => checked(++_nextZOrder);

    /// <summary>
    /// Keeps a normal window inside a work area. The declared minimum always wins (ADR 0095 §2):
    /// on a work area smaller than it, the window keeps its minimum size and overflows to the right
    /// and bottom, as on a small real screen, with its title bar kept reachable at the top-left.
    /// </summary>
    private static WindowBounds Constrain(WindowBounds requested, WindowConstraints constraints, WindowBounds workArea)
    {
        double minimumWidth = constraints.MinWidth;
        double minimumHeight = constraints.MinHeight;
        double maximumWidth = Math.Max(minimumWidth, Math.Min(constraints.MaxWidth ?? workArea.Width, workArea.Width));
        double maximumHeight = Math.Max(minimumHeight, Math.Min(constraints.MaxHeight ?? workArea.Height, workArea.Height));
        double width = Math.Clamp(requested.Width, minimumWidth, maximumWidth);
        double height = Math.Clamp(requested.Height, minimumHeight, maximumHeight);
        double x = Math.Max(workArea.X, Math.Min(requested.X, workArea.X + workArea.Width - width));
        double y = Math.Max(workArea.Y, Math.Min(requested.Y, workArea.Y + workArea.Height - height));
        return new WindowBounds(x, y, width, height);
    }

    private static WindowRuntimeState Copy(
        WindowRuntimeState state,
        WindowBounds? bounds = null,
        WindowBounds? restoreBounds = null,
        bool replaceRestoreBounds = false,
        int? zOrder = null,
        WindowVisualState? visualState = null,
        bool? isFocused = null,
        string? title = null,
        DisplayId? displayId = null) =>
        new(
            state.Id,
            state.AppId,
            state.OwnerInstanceId,
            title ?? state.Title,
            state.IconAssetPath,
            bounds ?? state.Bounds,
            replaceRestoreBounds ? restoreBounds : state.RestoreBounds,
            zOrder ?? state.ZOrder,
            visualState ?? state.VisualState,
            state.Constraints,
            state.Modality,
            state.OwnerId,
            isFocused ?? state.IsFocused,
            state.Content,
            state.OnRequestClose,
            state.SupportsBack)
        {
            IsResponding = state.IsResponding,
            DisplayId = displayId ?? state.DisplayId,
        };
}