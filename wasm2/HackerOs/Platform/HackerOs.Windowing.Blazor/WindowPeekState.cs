namespace HackerOs.Windowing.Blazor;

/// <summary>
/// How a window shows while the user peeks at one window from its taskbar preview (ADR 0199 §5):
/// the peeked window and its own dialogs stay, on top; the other windows of its screen turn
/// see-through, leaving only their outline.
/// </summary>
public enum WindowPeekState
{
    /// <summary>No peek: the window shows as usual.</summary>
    None = 0,

    /// <summary>The window is the one peeked at (or one of its dialogs): it shows, even minimized, above the others.</summary>
    Shown = 1,

    /// <summary>Another window of the same screen is peeked at: only this window's outline stays.</summary>
    SeeThrough = 2,
}
