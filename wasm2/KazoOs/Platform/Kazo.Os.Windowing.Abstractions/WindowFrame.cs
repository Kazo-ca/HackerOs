namespace Kazo.Os.Windowing.Abstractions;

/// <summary>
/// What the window manager draws around a window's client area (ADR 0234 §1), as
/// <c>FormBorderStyle</c> does for a WinForms form.
/// </summary>
public enum WindowFrameStyle
{
    /// <summary>Title bar, border and resize handles; the buttons follow <see cref="WindowFrame"/>.</summary>
    Normal,

    /// <summary>
    /// A shorter title bar with the close button only, no border and no resize handle, like
    /// <c>FixedToolWindow</c>. The OS dialogs use it (ADR 0234 §6).
    /// </summary>
    Tool,

    /// <summary>
    /// No title bar and no border: only the client area shows, on a transparent surface whose empty
    /// areas let clicks through (ADR 0234 §5). The app draws its own buttons and drag area.
    /// </summary>
    None,
}

/// <summary>
/// The frame options of one window (ADR 0234 §1-§2): its style and the WinForms-like switches that
/// choose which buttons the user sees. They set what the user can do with the frame, never what the
/// app may ask of its own window.
/// </summary>
/// <param name="Style">The frame drawn around the client area.</param>
/// <param name="ShowIcon">Whether the title bar shows the app icon.</param>
/// <param name="MaximizeBox">Whether the title bar offers Maximize (needs a resizable window).</param>
/// <param name="MinimizeBox">Whether the title bar offers Minimize.</param>
/// <param name="ShowInTaskbar">
/// Whether the window has a taskbar button. A window without one cannot be minimized by the user
/// (Benoit's answer to question 6 of ADR 0234): it would have nothing to come back from.
/// </param>
public sealed record WindowFrame(
    WindowFrameStyle Style = WindowFrameStyle.Normal,
    bool ShowIcon = true,
    bool MaximizeBox = true,
    bool MinimizeBox = true,
    bool ShowInTaskbar = true)
{
    /// <summary>Gets the frame every window had before ADR 0234: normal style, every switch on.</summary>
    public static WindowFrame Default { get; } = new();

    /// <summary>Gets the frame of the OS dialogs (ADR 0234 §6): tool style, no taskbar button.</summary>
    public static WindowFrame Dialog { get; } = new(WindowFrameStyle.Tool, ShowInTaskbar: false);

    /// <summary>Gets whether a title bar is drawn (every style but <see cref="WindowFrameStyle.None"/>).</summary>
    public bool HasTitleBar => Style != WindowFrameStyle.None;

    /// <summary>Gets whether the title bar shows the app icon.</summary>
    public bool ShowsIcon => Style == WindowFrameStyle.Normal && ShowIcon;

    /// <summary>Gets whether the user may minimize the window: Minimize button, menu entry, keyboard.</summary>
    public bool UserCanMinimize => Style == WindowFrameStyle.Normal && MinimizeBox && ShowInTaskbar;

    /// <summary>
    /// Gets whether the user may maximize a window with these constraints: Maximize button, double
    /// click on the title bar, snapping and keyboard.
    /// </summary>
    /// <param name="constraints">The window's size constraints.</param>
    public bool UserCanMaximize(WindowConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        return Style == WindowFrameStyle.Normal && MaximizeBox && constraints.IsResizable;
    }

    /// <summary>
    /// Gets whether the user may resize a window with these constraints through its frame: handles
    /// and keyboard resize. Only a normal frame has handles.
    /// </summary>
    /// <param name="constraints">The window's size constraints.</param>
    public bool UserCanResize(WindowConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        return Style == WindowFrameStyle.Normal && constraints.IsResizable;
    }
}
