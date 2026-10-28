using HackerOs.Windowing.Abstractions;

namespace HackerOs.Windowing.Blazor;

/// <summary>Which window menu the chrome asks for (ADR 0179 §3-4).</summary>
public enum WindowMenuKind
{
    /// <summary>The window menu: right click on the app icon, <c>Alt+Space</c>, <c>Alt+F3</c>.</summary>
    Window,

    /// <summary>The short menu of the Maximize/Restore button: snap, full screen, other screen.</summary>
    MaximizeButton,
}

/// <summary>
/// A request to show the window menu of one window (ADR 0095 §3), raised by the chrome.
/// </summary>
/// <param name="WindowId">Window whose menu is requested.</param>
/// <param name="ClientX">
/// Pointer position in viewport CSS pixels for a right click on the title bar, or
/// <see langword="null"/> when the menu opens from its button or the keyboard: the host then places it
/// under the window's top-left corner.
/// </param>
/// <param name="ClientY">Pointer position in viewport CSS pixels, or <see langword="null"/>.</param>
public sealed record WindowMenuRequest(WindowId WindowId, double? ClientX, double? ClientY)
{
    /// <summary>Gets which menu is requested; <see cref="WindowMenuKind.Window"/> by default.</summary>
    public WindowMenuKind Kind { get; init; } = WindowMenuKind.Window;
}
