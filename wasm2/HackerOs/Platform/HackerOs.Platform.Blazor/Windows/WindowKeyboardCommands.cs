using HackerOs.AppSdk.Globalization;
using HackerOs.Platform.Core.Globalization;
using HackerOs.Platform.Core.Settings;
using HackerOs.Platform.Core.Shell;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Blazor.Windows;

/// <summary>One key press forwarded by the browser while the window manager holds the keyboard.</summary>
/// <param name="Key"><c>KeyboardEvent.key</c>.</param>
/// <param name="Code"><c>KeyboardEvent.code</c>.</param>
/// <param name="Control">Ctrl held.</param>
/// <param name="Alt">Alt held.</param>
/// <param name="Shift">Shift held.</param>
/// <param name="Super">Super held.</param>
public sealed record WindowKeyInput(string Key, string Code, bool Control, bool Alt, bool Shift, bool Super);

/// <summary>What the window manager is doing with the keyboard right now.</summary>
public enum WindowKeyboardMode
{
    /// <summary>Nothing: only shortcuts are caught, every other key goes to the app.</summary>
    None,

    /// <summary>The window switcher is open; releasing Alt switches.</summary>
    Switching,

    /// <summary>The active window follows the arrow keys; Enter validates, Escape cancels.</summary>
    Moving,

    /// <summary>The active window's size follows the arrow keys; Enter validates, Escape cancels.</summary>
    Resizing,
}

/// <summary>One entry of a window menu.</summary>
/// <param name="Action">Command run by the entry.</param>
/// <param name="Label">Text of the entry.</param>
/// <param name="Shortcut">Shortcut shown next to it, or <see langword="null"/>.</param>
/// <param name="IsEnabled">Whether the command applies to the window in its current state.</param>
public sealed record WindowMenuItem(WindowKeyboardAction Action, string Label, string? Shortcut, bool IsEnabled);

/// <summary>An open window menu.</summary>
/// <param name="WindowId">Window the menu acts on.</param>
/// <param name="X">Left position in viewport CSS pixels.</param>
/// <param name="Y">Top position in viewport CSS pixels.</param>
public sealed record WindowMenuState(WindowId WindowId, double X, double Y);

/// <summary>
/// The window manager's keyboard commands (ADR 0095 §3): shortcuts read from
/// <c>keybindings.config</c>, the window switcher, keyboard move and resize, and the window menu.
/// </summary>
/// <remarks>
/// <para>
/// All decisions are made here, in C# (ADR 0009). The browser side (<c>WindowManagerOverlays.razor.js</c>)
/// only matches the flat list in <see cref="ActiveBindings"/> in the capture phase, so window commands
/// pass before any app, like keys grabbed by an X11 window manager, and forwards every key while
/// <see cref="Mode"/> is not <see cref="WindowKeyboardMode.None"/>.
/// </para>
/// <para>
/// The Linux keys are part of <see cref="ActiveBindings"/> only while <see cref="IsKeyboardLocked"/>:
/// outside full screen the host system keeps Alt+Tab, Alt+F4 and Super for itself.
/// </para>
/// </remarks>
public sealed class WindowKeyboardCommands : IDisposable
{
    /// <summary>Distance a window moves or grows per arrow key, in CSS pixels.</summary>
    public const double KeyboardStep = 10;

    /// <summary>Distance with Ctrl held, for fine placement.</summary>
    public const double FineKeyboardStep = 1;

    private readonly WindowRuntime _runtime;
    private readonly IOsSettingsService? _settings;
    private readonly ILogger _logger;
    private readonly Microsoft.Extensions.Localization.IStringLocalizer _text;
    private IReadOnlyList<WindowKeyBinding> _bindings = WindowKeyBindingsSettingsDocuments.DefaultBindings;
    private IReadOnlyList<WindowId> _switchOrder = [];
    private int _switchIndex;
    private WindowId? _modeWindow;
    private WindowBounds _modeOrigin;
    private bool _started;

    /// <summary>Creates the commands over the desktop's window runtime.</summary>
    /// <param name="runtime">Authoritative window runtime.</param>
    /// <param name="settings">OS settings, to read <c>keybindings.config</c>; without it the defaults apply.</param>
    /// <param name="logger">Receives the lines of the file that could not be read.</param>
    /// <param name="locale">The session language of the window menu (ADR 0104); English without it.</param>
    public WindowKeyboardCommands(
        WindowRuntime runtime,
        IOsSettingsService? settings = null,
        ILogger<WindowKeyboardCommands>? logger = null,
        SessionLocaleService? locale = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _settings = settings;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _text = Strings.For(() => locale?.Culture ?? HackLocale.DefaultLanguage);
    }

    /// <summary>Raised when the mode, the switcher, the menu or the active bindings changed.</summary>
    public event Action? Changed;

    /// <summary>Gets what the keyboard is doing right now.</summary>
    public WindowKeyboardMode Mode { get; private set; }

    /// <summary>Gets whether HackerOS holds the host keyboard lock (full screen), which enables the Linux keys.</summary>
    public bool IsKeyboardLocked { get; private set; }

    /// <summary>Gets the open window menu, or <see langword="null"/>.</summary>
    public WindowMenuState? Menu { get; private set; }

    /// <summary>Gets or sets the shell's close routine, which runs the window's close confirmation (ADR 0006).</summary>
    public Func<WindowId, Task>? CloseWindow { get; set; }

    /// <summary>Gets or sets the routine that enters or leaves full screen (keyboard lock), when the host offers one.</summary>
    public Func<Task>? ToggleFullScreen { get; set; }

    /// <summary>Gets every binding read from the file, active or not.</summary>
    public IReadOnlyList<WindowKeyBinding> Bindings => _bindings;

    /// <summary>Gets the bindings the browser must catch now: the replacement keys, plus the Linux keys under the keyboard lock.</summary>
    public IReadOnlyList<WindowKeyBinding> ActiveBindings =>
        [.. _bindings.Where(binding => binding.Set == WindowKeyBindingSet.Fallback || IsKeyboardLocked)];

    /// <summary>Gets the windows shown by the switcher, front to back.</summary>
    public IReadOnlyList<WindowRuntimeState> SwitcherWindows =>
        [.. _switchOrder.Select(id => _runtime.Windows.FirstOrDefault(window => window.Id == id)).OfType<WindowRuntimeState>()];

    /// <summary>Gets the window selected in the switcher, or <see langword="null"/>.</summary>
    public WindowId? SwitcherSelection =>
        Mode == WindowKeyboardMode.Switching && _switchOrder.Count > 0 ? _switchOrder[_switchIndex] : null;

    /// <summary>Gets the window being moved or resized from the keyboard, or <see langword="null"/>.</summary>
    public WindowId? ModeWindow => Mode is WindowKeyboardMode.Moving or WindowKeyboardMode.Resizing ? _modeWindow : null;

    /// <summary>Reads <c>keybindings.config</c> and follows its changes. Safe to call more than once.</summary>
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
            await _settings.WatchAsync(WindowKeyBindingsSettingsDocuments.Document, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "keybindings: changes to keybindings.config are not followed");
        }
    }

    /// <summary>Gets whether the OS is shown full screen in the host browser.</summary>
    public bool IsFullScreen { get; private set; }

    /// <summary>Records the host's full-screen and keyboard-lock state, reported by the browser.</summary>
    /// <param name="fullScreen">Whether the OS is full screen.</param>
    /// <param name="locked">Whether the host granted the keyboard lock.</param>
    public void SetHostKeyboardState(bool fullScreen, bool locked)
    {
        if (IsFullScreen != fullScreen || IsKeyboardLocked != locked)
        {
            IsFullScreen = fullScreen;
            IsKeyboardLocked = locked;
            Changed?.Invoke();
        }
    }

    /// <summary>Runs one window command on the active window (or the switcher).</summary>
    /// <param name="action">Command to run.</param>
    /// <returns><see langword="true"/> when the command applied.</returns>
    public Task<bool> ExecuteAsync(WindowKeyboardAction action)
    {
        if (action is WindowKeyboardAction.SwitchNext or WindowKeyboardAction.SwitchPrevious)
        {
            return Task.FromResult(Switch(action == WindowKeyboardAction.SwitchNext));
        }

        if (action == WindowKeyboardAction.ToggleFullScreen)
        {
            return ToggleFullScreenAsync();
        }

        return ActiveWindow() is { } window
            ? ExecuteForWindowAsync(action, window.Id)
            : Task.FromResult(false);
    }

    /// <summary>Runs one window command on a given window (a window menu entry).</summary>
    /// <param name="action">Command to run.</param>
    /// <param name="windowId">Target window.</param>
    /// <returns><see langword="true"/> when the command applied.</returns>
    public async Task<bool> ExecuteForWindowAsync(WindowKeyboardAction action, WindowId windowId)
    {
        CloseMenu();
        if (Find(windowId) is not { } window || _runtime.IsInteractionBlocked(windowId))
        {
            return false;
        }

        bool resizable = window.Constraints.IsResizable;
        switch (action)
        {
            case WindowKeyboardAction.SwitchNext or WindowKeyboardAction.SwitchPrevious:
                return Switch(action == WindowKeyboardAction.SwitchNext);
            case WindowKeyboardAction.Close:
                if (CloseWindow is not null)
                {
                    await CloseWindow(windowId).ConfigureAwait(true);
                    return true;
                }

                return false;
            case WindowKeyboardAction.WindowMenu:
                OpenMenu(windowId, null, null);
                return true;
            case WindowKeyboardAction.Move:
                return BeginMode(window, WindowKeyboardMode.Moving);
            case WindowKeyboardAction.Resize:
                return BeginMode(window, WindowKeyboardMode.Resizing);
            case WindowKeyboardAction.Minimize:
                return Apply(new MinimizeWindowCommand(windowId));
            case WindowKeyboardAction.Maximize:
                return resizable && window.VisualState != WindowVisualState.Maximized && Apply(new MaximizeWindowCommand(windowId));
            case WindowKeyboardAction.RestoreOrMinimize:
                return window.VisualState == WindowVisualState.Maximized || window.IsSnapped
                    ? Apply(new RestoreWindowCommand(windowId))
                    : Apply(new MinimizeWindowCommand(windowId));
            case WindowKeyboardAction.ToggleMaximize:
                if (window.VisualState == WindowVisualState.Maximized)
                {
                    return Apply(new RestoreWindowCommand(windowId));
                }

                return resizable && Apply(new MaximizeWindowCommand(windowId));
            case WindowKeyboardAction.SnapLeft or WindowKeyboardAction.SnapRight:
                return resizable && Snap(window, action == WindowKeyboardAction.SnapLeft);
            case WindowKeyboardAction.ToggleFullScreen:
                if (ToggleFullScreen is not null)
                {
                    await ToggleFullScreen().ConfigureAwait(true);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>Handles a key forwarded while the window manager holds the keyboard.</summary>
    /// <param name="input">The key press.</param>
    /// <returns><see langword="true"/> when the key was used.</returns>
    public bool HandleGrabbedKey(WindowKeyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        switch (Mode)
        {
            case WindowKeyboardMode.Switching:
                if (input.Key == "Escape")
                {
                    EndSwitch(commit: false);
                    return true;
                }

                if (input.Key == "Enter")
                {
                    EndSwitch(commit: true);
                    return true;
                }

                // Any binding of a switch action cycles while the switcher is open, locked or not.
                foreach (WindowKeyBinding binding in _bindings)
                {
                    if (binding.Action is WindowKeyboardAction.SwitchNext or WindowKeyboardAction.SwitchPrevious
                        && binding.Chord.Matches(input.Control, input.Alt, input.Shift, input.Super, input.Key, input.Code))
                    {
                        return Switch(binding.Action == WindowKeyboardAction.SwitchNext);
                    }
                }

                return input.Key is "ArrowRight" or "ArrowDown"
                    ? Switch(forward: true)
                    : input.Key is "ArrowLeft" or "ArrowUp" && Switch(forward: false);
            case WindowKeyboardMode.Moving or WindowKeyboardMode.Resizing:
                return HandleModeKey(input);
            default:
                return false;
        }
    }

    /// <summary>Alt was released: an open switcher switches to its selection.</summary>
    public void HandleAltReleased()
    {
        if (Mode == WindowKeyboardMode.Switching)
        {
            EndSwitch(commit: true);
        }
    }

    /// <summary>Opens the window menu of a window.</summary>
    /// <param name="windowId">Window.</param>
    /// <param name="clientX">Pointer position, or <see langword="null"/> to place it at the window's corner.</param>
    /// <param name="clientY">Pointer position, or <see langword="null"/>.</param>
    public void OpenMenu(WindowId windowId, double? clientX, double? clientY)
    {
        if (Find(windowId) is not { } window)
        {
            return;
        }

        CancelMode();
        WindowBounds anchor = window.VisualState == WindowVisualState.Maximized ? _runtime.WorkArea : window.Bounds;
        Menu = new WindowMenuState(windowId, clientX ?? anchor.X + 4, clientY ?? anchor.Y + 32);
        Changed?.Invoke();
    }

    /// <summary>Closes the window menu.</summary>
    public void CloseMenu()
    {
        if (Menu is not null)
        {
            Menu = null;
            Changed?.Invoke();
        }
    }

    /// <summary>Gets the entries of a window's menu, enabled according to its state.</summary>
    /// <param name="windowId">Window.</param>
    public IReadOnlyList<WindowMenuItem> GetMenuItems(WindowId windowId)
    {
        if (Find(windowId) is not { } window)
        {
            return [];
        }

        bool normal = window.VisualState == WindowVisualState.Normal;
        bool maximized = window.VisualState == WindowVisualState.Maximized;
        bool resizable = window.Constraints.IsResizable;
        return
        [
            new(WindowKeyboardAction.ToggleMaximize, _text[maximized ? "WindowMenu.Restore" : "WindowMenu.Maximize"], ShortcutOf(WindowKeyboardAction.ToggleMaximize), maximized || resizable),
            new(WindowKeyboardAction.Minimize, _text["WindowMenu.Minimize"], ShortcutOf(WindowKeyboardAction.Minimize), true),
            new(WindowKeyboardAction.Move, _text["WindowMenu.Move"], ShortcutOf(WindowKeyboardAction.Move), normal && window.Constraints.IsMovable),
            new(WindowKeyboardAction.Resize, _text["WindowMenu.Resize"], ShortcutOf(WindowKeyboardAction.Resize), normal && resizable),
            new(WindowKeyboardAction.SnapLeft, _text["WindowMenu.SnapLeft"], ShortcutOf(WindowKeyboardAction.SnapLeft), resizable),
            new(WindowKeyboardAction.SnapRight, _text["WindowMenu.SnapRight"], ShortcutOf(WindowKeyboardAction.SnapRight), resizable),
            new(WindowKeyboardAction.ToggleFullScreen, _text[IsFullScreen ? "WindowMenu.ExitFullScreen" : "WindowMenu.FullScreen"], ShortcutOf(WindowKeyboardAction.ToggleFullScreen), ToggleFullScreen is not null),
            new(WindowKeyboardAction.Close, _text["WindowMenu.Close"], ShortcutOf(WindowKeyboardAction.Close), true),
        ];
    }

    /// <summary>
    /// Gets the shortcut shown for an action: the first active chord, so a menu never advertises a
    /// key the host system would swallow.
    /// </summary>
    /// <param name="action">Action.</param>
    public string? ShortcutOf(WindowKeyboardAction action) =>
        ActiveBindings
            .Where(binding => binding.Action == action)
            .OrderBy(binding => binding.Set == WindowKeyBindingSet.Linux ? 0 : 1)
            .Select(binding => binding.Chord.ToString())
            .FirstOrDefault();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_settings is not null)
        {
            _settings.DocumentChanged -= OnSettingsChanged;
        }
    }

    private async Task<bool> ToggleFullScreenAsync()
    {
        CloseMenu();
        if (ToggleFullScreen is null)
        {
            return false;
        }

        await ToggleFullScreen().ConfigureAwait(true);
        return true;
    }

    private WindowRuntimeState? ActiveWindow() => _runtime.Windows.FirstOrDefault(window => window.IsFocused);

    private WindowRuntimeState? Find(WindowId id) => _runtime.Windows.FirstOrDefault(window => window.Id == id);

    private bool Apply(WindowCommand command)
    {
        try
        {
            return _runtime.Apply(command).Count > 0;
        }
        catch (InvalidOperationException)
        {
            // The window's state changed under the command (a minimize racing a move): nothing to do.
            return false;
        }
    }

    private bool Switch(bool forward)
    {
        CloseMenu();
        if (Mode != WindowKeyboardMode.Switching)
        {
            CancelMode();
            _switchOrder =
            [
                .. _runtime.Windows
                    .Where(window => !_runtime.IsInteractionBlocked(window.Id))
                    .OrderByDescending(window => window.VisualState != WindowVisualState.Minimized)
                    .ThenByDescending(window => window.ZOrder)
                    .Select(window => window.Id),
            ];
            if (_switchOrder.Count == 0)
            {
                return false;
            }

            Mode = WindowKeyboardMode.Switching;
            _switchIndex = _switchOrder.Count == 1 ? 0 : forward ? 1 : _switchOrder.Count - 1;
        }
        else
        {
            _switchIndex = (_switchIndex + (forward ? 1 : -1) + _switchOrder.Count) % _switchOrder.Count;
        }

        Changed?.Invoke();
        return true;
    }

    private void EndSwitch(bool commit)
    {
        WindowId? target = SwitcherSelection;
        Mode = WindowKeyboardMode.None;
        _switchOrder = [];
        if (commit && target is WindowId id && Find(id) is { } window)
        {
            if (window.VisualState == WindowVisualState.Minimized)
            {
                Apply(new RestoreWindowCommand(id));
            }

            Apply(new FocusWindowCommand(id));
        }

        Changed?.Invoke();
    }

    private bool BeginMode(WindowRuntimeState window, WindowKeyboardMode mode)
    {
        bool allowed = window.VisualState == WindowVisualState.Normal
            && (mode == WindowKeyboardMode.Moving ? window.Constraints.IsMovable : window.Constraints.IsResizable);
        if (!allowed)
        {
            return false;
        }

        CancelMode();
        Mode = mode;
        _modeWindow = window.Id;
        _modeOrigin = window.Bounds;
        Changed?.Invoke();
        return true;
    }

    private bool HandleModeKey(WindowKeyInput input)
    {
        if (_modeWindow is not WindowId id || Find(id) is not { } window || window.VisualState != WindowVisualState.Normal)
        {
            EndMode(commit: false);
            return false;
        }

        switch (input.Key)
        {
            case "Enter":
                EndMode(commit: true);
                return true;
            case "Escape":
                EndMode(commit: false);
                return true;
        }

        double step = input.Control ? FineKeyboardStep : KeyboardStep;
        (double dx, double dy) = input.Key switch
        {
            "ArrowLeft" => (-step, 0d),
            "ArrowRight" => (step, 0d),
            "ArrowUp" => (0d, -step),
            "ArrowDown" => (0d, step),
            _ => (0d, 0d),
        };
        if (dx == 0 && dy == 0)
        {
            return true;
        }

        WindowBounds bounds = window.Bounds;
        if (Mode == WindowKeyboardMode.Moving)
        {
            Apply(new MoveWindowCommand(id, bounds.X + dx, bounds.Y + dy));
        }
        else
        {
            Apply(new ResizeWindowCommand(id, new WindowBounds(
                bounds.X, bounds.Y, Math.Max(1, bounds.Width + dx), Math.Max(1, bounds.Height + dy))));
        }

        return true;
    }

    private void EndMode(bool commit)
    {
        WindowKeyboardMode mode = Mode;
        WindowId? id = _modeWindow;
        Mode = WindowKeyboardMode.None;
        _modeWindow = null;
        if (id is WindowId windowId && Find(windowId) is { VisualState: WindowVisualState.Normal })
        {
            if (!commit)
            {
                Apply(mode == WindowKeyboardMode.Moving
                    ? new MoveWindowCommand(windowId, _modeOrigin.X, _modeOrigin.Y)
                    : new ResizeWindowCommand(windowId, _modeOrigin));
            }

            if (mode == WindowKeyboardMode.Resizing)
            {
                Apply(new CompleteWindowResizeCommand(windowId));
            }
        }

        Changed?.Invoke();
    }

    private void CancelMode()
    {
        if (Mode is WindowKeyboardMode.Moving or WindowKeyboardMode.Resizing)
        {
            EndMode(commit: false);
        }
        else if (Mode == WindowKeyboardMode.Switching)
        {
            EndSwitch(commit: false);
        }
    }

    /// <summary>
    /// Tiles a window on one half of the work area. The runtime keeps its geometry from before the
    /// snap, which a restore or a title-bar drag gives back (ADR 0177).
    /// </summary>
    private bool Snap(WindowRuntimeState window, bool left)
    {
        WindowBounds area = _runtime.WorkArea;
        double width = Math.Floor(area.Width / 2);
        WindowBounds half = new(left ? area.X : area.X + area.Width - width, area.Y, width, area.Height);
        return Apply(new SnapWindowCommand(window.Id, half));
    }

    private void OnSettingsChanged(object? sender, OsSettingsChangedEventArgs args)
    {
        if (args.Concerns(WindowKeyBindingsSettingsDocuments.DocumentId))
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
                .ReadAsync(WindowKeyBindingsSettingsDocuments.Document, cancellationToken)
                .ConfigureAwait(false);
            _bindings = WindowKeyBindingsSettingsDocuments.ToBindings(snapshot, out IReadOnlyList<string> problems);
            foreach (string problem in problems)
            {
                _logger.LogWarning("{Problem}", problem);
            }

            foreach (LayeredConfigWarning warning in snapshot.Warnings)
            {
                _logger.LogWarning("{Warning}", warning.ToLogLine());
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "keybindings: keybindings.config could not be read, using the defaults");
            _bindings = WindowKeyBindingsSettingsDocuments.DefaultBindings;
        }

        Changed?.Invoke();
    }
}
