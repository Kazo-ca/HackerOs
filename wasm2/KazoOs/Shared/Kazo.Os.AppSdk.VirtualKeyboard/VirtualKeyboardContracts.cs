namespace Kazo.Os.AppSdk.VirtualKeyboard;

/// <summary>
/// Identifies a key produced by <see cref="VirtualKeyboard"/>, independent of any specific
/// consumer. Shaped to mirror a terminal-style renderer-independent key contract so a host can
/// map it 1:1 onto its own domain, without this package depending on that domain.
/// </summary>
public enum VirtualKey
{
    /// <summary>A printable character; <see cref="VirtualKeyPressedEvent.Text"/> carries it.</summary>
    Character,

    /// <summary>The Enter/Return key.</summary>
    Enter,

    /// <summary>Deletes the character before the cursor.</summary>
    Backspace,

    /// <summary>Deletes the character at/after the cursor.</summary>
    Delete,

    /// <summary>The Tab key.</summary>
    Tab,

    /// <summary>The Escape key.</summary>
    Escape,

    /// <summary>Moves left.</summary>
    ArrowLeft,

    /// <summary>Moves right.</summary>
    ArrowRight,

    /// <summary>Moves up.</summary>
    ArrowUp,

    /// <summary>Moves down.</summary>
    ArrowDown,

    /// <summary>Moves to the start of the line.</summary>
    Home,

    /// <summary>Moves to the end of the line.</summary>
    End,

    /// <summary>Scrolls up one page.</summary>
    PageUp,

    /// <summary>Scrolls down one page.</summary>
    PageDown,
}

/// <summary>
/// Describes one key press raised by <see cref="VirtualKeyboard"/>. The control only raises this
/// event through <see cref="VirtualKeyboard.OnKeyPressed"/> — it never writes into a focused
/// element itself; the host decides what the event means for its own target.
/// </summary>
public sealed record VirtualKeyPressedEvent(
    VirtualKey Key,
    string? Text = null,
    bool Control = false,
    bool Alt = false,
    bool Shift = false);
