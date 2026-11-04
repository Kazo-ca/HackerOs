namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// Visible state of an app's window (ADR 0095 §1), the SDK's own copy of the window manager's
/// state so apps never reference <c>Kazo.Os.Windowing.*</c> (ADR 0055).
/// </summary>
public enum KazoWindowState
{
    /// <summary>The window has its own position and size on the desktop.</summary>
    Normal,

    /// <summary>The window is hidden in the taskbar. The app keeps running.</summary>
    Minimized,

    /// <summary>The window fills the desktop's work area.</summary>
    Maximized,
}

/// <summary>
/// Size of the client area of an app's window (ADR 0095 §1): the part the app draws in, without the
/// title bar and borders, in CSS pixels.
/// </summary>
/// <param name="Width">Client width in CSS pixels.</param>
/// <param name="Height">Client height in CSS pixels.</param>
public readonly record struct KazoWindowSize(double Width, double Height)
{
    /// <summary>
    /// Gets the detached display surface whose host window was resized (ADR 0078), or
    /// <see langword="null"/> for the app's window on the desktop. Only set for surfaces the app
    /// follows with <see cref="WindowAppBase.TrackDisplaySurface"/>.
    /// </summary>
    public string? DisplaySurfaceId { get; init; }
}

/// <summary>
/// Receives the window manager's notifications for one window app (ADR 0095 §1).
/// </summary>
/// <remarks>
/// Implemented by <see cref="WindowAppBase"/>, which turns each call into its <c>OnWindow*</c> hooks on
/// the app's render thread. The platform calls it, in the order of the window manager's events; an
/// app never implements nor calls it.
/// </remarks>
public interface IWindowAppEventTarget
{
    /// <summary>The window gained the keyboard focus.</summary>
    Task NotifyActivatedAsync();

    /// <summary>The window lost the keyboard focus.</summary>
    Task NotifyDeactivatedAsync();

    /// <summary>The window was minimized, maximized or restored.</summary>
    /// <param name="previous">State before the change.</param>
    /// <param name="current">State after the change.</param>
    Task NotifyStateChangedAsync(KazoWindowState previous, KazoWindowState current);

    /// <summary>The window's client area has a new size (end of a resize, maximize, restore).</summary>
    /// <param name="size">New client size.</param>
    Task NotifyResizedAsync(KazoWindowSize size);
}
