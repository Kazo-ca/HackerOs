using HackerOs.Windowing.Core;
using HackerOs.Windowing.Abstractions;

namespace HackerOs.Taskbar.Blazor;

/// <summary>Describes one running window as the taskbar needs to render it.</summary>
public sealed record TaskbarWindowItem(
    WindowId Id,
    string Title,
    string? IconAssetPath,
    bool IsFocused,
    bool IsMinimized);

/// <summary>
/// Provides the ordered set of windows the taskbar should list. The taskbar renders nothing in
/// its running-applications region when no source is supplied.
/// </summary>
public interface ITaskbarWindowSource
{
    /// <summary>Gets the windows to render, in display order.</summary>
    IReadOnlyList<TaskbarWindowItem> Windows { get; }

    /// <summary>Raised when the window list or any listed window's state has changed.</summary>
    event Action? Changed;
}

/// <summary>Dispatches taskbar-initiated window commands. The host owns how these are fulfilled.</summary>
public interface ITaskbarCommandDispatcher
{
    /// <summary>Requests focus for one window, bringing it to the front.</summary>
    void Activate(WindowId id);

    /// <summary>Requests taskbar minimization for one window.</summary>
    void Minimize(WindowId id);

    /// <summary>Requests restoration of one minimized window to its normal state.</summary>
    void Restore(WindowId id);

    /// <summary>Requests a graceful close for one window.</summary>
    void Close(WindowId id);
}

/// <summary>
/// Controls the host-provided application launcher surface. The taskbar renders no launcher
/// trigger when no launcher is supplied.
/// </summary>
public interface ITaskbarLauncher
{
    /// <summary>Gets whether the launcher is currently open.</summary>
    bool IsOpen { get; }

    /// <summary>Requests the launcher be opened or closed.</summary>
    void Toggle();

    /// <summary>Raised when <see cref="IsOpen"/> changes for a reason external to the taskbar (e.g. Escape).</summary>
    event Action? Changed;

    /// <summary>
    /// Gets whether the launcher has something new to show: the trigger then carries a dot (ADR 0186
    /// §4.5, changes the user has not seen). <see cref="Changed"/> is raised when it changes.
    /// </summary>
    bool HasAttention => false;
}

/// <summary>
/// Supplies status content for the taskbar's status region (starting with the clock; network,
/// battery, or other host-defined status can extend this later). No status region renders when
/// no source is supplied.
/// </summary>
public interface ITaskbarStatusSource
{
    /// <summary>Gets the current time to render in the taskbar clock.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Gets the culture the clock is formatted with (its long time pattern), e.g. the session's
    /// <c>LC_TIME</c> culture (ADR 0104). Hosts that do not localize keep the invariant
    /// <c>HH:mm:ss</c>.
    /// </summary>
    System.Globalization.CultureInfo FormatCulture => System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>
    /// Gets the time zone the clock shows <see cref="UtcNow"/> in, e.g. the session's zone
    /// (ADR 0193). Hosts that choose no zone keep UTC.
    /// </summary>
    TimeZoneInfo TimeZone => TimeZoneInfo.Utc;

    /// <summary>Raised when the status has changed and the taskbar should re-render.</summary>
    event Action? Changed;
}

/// <summary>
/// Controls the host-provided clock panel (notifications, calendar, and platform-mode toggle,
/// among other host-defined content). No clock trigger renders when no source is supplied.
/// </summary>
public interface ITaskbarClockPanelSource
{
    /// <summary>Gets whether the clock panel is currently open.</summary>
    bool IsOpen { get; }

    /// <summary>Requests the clock panel be opened or closed.</summary>
    void Toggle();

    /// <summary>Raised when <see cref="IsOpen"/> changes for a reason external to the taskbar (e.g. Escape).</summary>
    event Action? Changed;
}

/// <summary>
/// Supplies optional session-level commands. No session control renders when none is supplied.
/// </summary>
public interface ITaskbarSessionCommands
{
    /// <summary>Requests the current session be signed out.</summary>
    void RequestLogout();
}

/// <summary>
/// Supplies the host's context menus (ADR 0194 §1): HTML attributes the taskbar sets on itself and
/// on the button of each window, which the host's own context menu system reads. The taskbar never
/// inspects them; it keeps the browser's menu when no instance is supplied.
/// </summary>
public interface ITaskbarContextMenus
{
    /// <summary>Gets the attributes of the taskbar itself (its empty area), or <see langword="null"/> for none.</summary>
    IReadOnlyDictionary<string, object>? TaskbarAttributes { get; }

    /// <summary>Gets the attributes of the button of one window, or <see langword="null"/> for none.</summary>
    /// <param name="id">The window the button stands for.</param>
    IReadOnlyDictionary<string, object>? WindowItemAttributes(WindowId id);
}

/// <summary>
/// Host-supplied labels for taskbar chrome that would otherwise hardcode HackerOS branding.
/// Zone visibility is controlled by which source parameters the host supplies, not by options.
/// </summary>
public sealed record TaskbarOptions
{
    /// <summary>Gets the short glyph shown in the launcher trigger.</summary>
    public string LauncherMark { get; init; } = "H_";

    /// <summary>
    /// Gets the label shown next to the launcher trigger, or <see langword="null"/> (the default)
    /// for the taskbar's own localized "Applications".
    /// </summary>
    public string? LauncherLabel { get; init; }
}
