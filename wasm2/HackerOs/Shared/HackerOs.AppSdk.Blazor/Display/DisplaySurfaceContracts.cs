using Microsoft.AspNetCore.Components;

namespace HackerOs.AppSdk.Blazor.Display;

/// <summary>Lifecycle of a detached display surface (ADR 0078 §2).</summary>
public enum DisplaySurfaceState
{
    /// <summary>The host window is open and shows the surface.</summary>
    Open,

    /// <summary>The user closed the host window (its close button, Alt+F4…).</summary>
    ClosedByUser,

    /// <summary>The OS closed the surface: its owner asked, or the session ended.</summary>
    Closed,

    /// <summary>The host window left the surface's document (the user navigated it elsewhere) and was let go.</summary>
    Lost,
}

/// <summary>How a request to open a detached surface ended.</summary>
public enum DisplaySurfaceOpenStatus
{
    /// <summary>The host window opened; <see cref="DisplaySurfaceOpenResult.Surface"/> is set.</summary>
    Opened,

    /// <summary>The host browser refused the new window (pop-up blocker, no user gesture). The OS posted a notification.</summary>
    Blocked,

    /// <summary>This host cannot open windows at all (no JavaScript runtime, prerendering, tests).</summary>
    Unavailable,
}

/// <summary>Parameters of a new detached surface.</summary>
/// <param name="Title">Title of the host window (its title bar and the host's task switcher).</param>
/// <param name="Width">Requested width of the host window, in CSS pixels; the host browser may adjust it.</param>
/// <param name="Height">Requested height of the host window, in CSS pixels.</param>
public sealed record DisplaySurfaceOptions(string Title, int Width = 1024, int Height = 768);

/// <summary>Result of <see cref="IDisplaySurfaceService.OpenAsync"/>.</summary>
/// <param name="Status">Whether the surface opened.</param>
/// <param name="Surface">The new surface when <paramref name="Status"/> is <see cref="DisplaySurfaceOpenStatus.Opened"/>.</param>
public sealed record DisplaySurfaceOpenResult(DisplaySurfaceOpenStatus Status, IDisplaySurface? Surface)
{
    /// <summary>A result for a host that cannot open windows.</summary>
    public static DisplaySurfaceOpenResult Unavailable { get; } = new(DisplaySurfaceOpenStatus.Unavailable, null);
}

/// <summary>
/// A detached display surface (ADR 0078): a window of the host browser, outside the OS desktop,
/// that the running OS draws into. It runs no second OS: the content shown there is rendered by the
/// main instance's Blazor renderer and its events come back to it, like a second monitor plugged
/// into the same machine.
/// </summary>
/// <remarks>
/// Content reaches a surface through <see cref="DisplaySurfaceContent"/>, which moves its rendered
/// markup into the surface and keeps it live. A component that holds a surface closes it when it is
/// disposed: a surface belongs to whoever opened it.
/// </remarks>
public interface IDisplaySurface
{
    /// <summary>Gets the identifier of the surface, unique in the session (<c>surface-1</c>, <c>surface-2</c>…).</summary>
    string Id { get; }

    /// <summary>Gets the current title of the host window.</summary>
    string Title { get; }

    /// <summary>Gets the current inner width of the host window, in CSS pixels.</summary>
    int Width { get; }

    /// <summary>Gets the current inner height of the host window, in CSS pixels.</summary>
    int Height { get; }

    /// <summary>Gets whether the host window has the keyboard focus.</summary>
    bool HasFocus { get; }

    /// <summary>Gets the state of the surface; only <see cref="DisplaySurfaceState.Open"/> shows content.</summary>
    DisplaySurfaceState State { get; }

    /// <summary>
    /// Raised after the size, the focus, the title or the state changed. Raised outside any Blazor
    /// event and outside any app's ambient scope: a component marshals it with <c>InvokeAsync</c>.
    /// </summary>
    event Action<IDisplaySurface>? Changed;

    /// <summary>Changes the title of the host window.</summary>
    /// <param name="title">The new title.</param>
    ValueTask SetTitleAsync(string title);

    /// <summary>Brings the host window to the front, when the host browser allows it.</summary>
    ValueTask FocusAsync();

    /// <summary>Closes the host window; the state becomes <see cref="DisplaySurfaceState.Closed"/>.</summary>
    ValueTask CloseAsync();

    /// <summary>Moves rendered content into the surface. Called by <see cref="DisplaySurfaceContent"/>.</summary>
    /// <param name="content">The element whose subtree is shown in the surface.</param>
    ValueTask MountAsync(ElementReference content);

    /// <summary>Gives rendered content back to where it was rendered. Called by <see cref="DisplaySurfaceContent"/>.</summary>
    /// <param name="content">An element passed to <see cref="MountAsync"/>.</param>
    ValueTask UnmountAsync(ElementReference content);
}

/// <summary>
/// Opens and tracks the detached display surfaces of the session (ADR 0078 §2). The desktop is the
/// implicit primary surface; this service manages the others. Optional: a host without it (unit
/// tests, prerendering) simply cannot detach content.
/// </summary>
public interface IDisplaySurfaceService
{
    /// <summary>Gets the surfaces currently open, in opening order.</summary>
    IReadOnlyList<IDisplaySurface> Surfaces { get; }

    /// <summary>Raised after a surface opened or ended. Raised outside any Blazor event.</summary>
    event Action? SurfacesChanged;

    /// <summary>
    /// Opens a new host window. Call it from a user gesture (a click, a key press): host browsers
    /// block windows opened without one. A blocked window posts an OS notification explaining how
    /// to allow pop-ups for KazoOS.
    /// </summary>
    /// <param name="options">Title and size of the new window.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<DisplaySurfaceOpenResult> OpenAsync(DisplaySurfaceOptions options, CancellationToken cancellationToken = default);

    /// <summary>Closes every surface (log out, shut down).</summary>
    Task CloseAllAsync();
}
