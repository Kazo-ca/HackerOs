namespace Kazo.Os.AppSdk;

/// <summary>Identifies renderer-independent keys delivered to a full-screen terminal command.</summary>
public enum TerminalKey
{
    /// <summary>A printable character; the text is in <see cref="TerminalKeyEvent.Text"/>.</summary>
    Character,
    /// <summary>The Enter (Return) key.</summary>
    Enter,
    /// <summary>Backspace: deletes the character before the cursor.</summary>
    Backspace,
    /// <summary>Delete: deletes the character under the cursor.</summary>
    Delete,
    /// <summary>The left arrow key.</summary>
    ArrowLeft,
    /// <summary>The right arrow key.</summary>
    ArrowRight,
    /// <summary>The up arrow key.</summary>
    ArrowUp,
    /// <summary>The down arrow key.</summary>
    ArrowDown,
    /// <summary>The Home key.</summary>
    Home,
    /// <summary>The End key.</summary>
    End,
    /// <summary>The Page Up key.</summary>
    PageUp,
    /// <summary>The Page Down key.</summary>
    PageDown,
    /// <summary>Not a key: the terminal viewport changed size and the command should redraw, like <c>SIGWINCH</c>.</summary>
    Resize,
    /// <summary>The Escape key.</summary>
    Escape
}

/// <summary>Describes one keyboard event without depending on a browser renderer.</summary>
public sealed record TerminalKeyEvent(
    TerminalKey Key,
    string? Text = null,
    bool Control = false,
    bool Alt = false,
    bool Shift = false);

/// <summary>Describes the current character-cell viewport.</summary>
public readonly record struct TerminalViewport(int Columns, int Rows)
{
    /// <summary>Creates a validated terminal viewport.</summary>
    public static TerminalViewport Create(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        return new TerminalViewport(columns, rows);
    }
}

/// <summary>Describes a cursor location within a rendered frame.</summary>
public readonly record struct TerminalCursor(int Column, int Row, bool Visible = true);

/// <summary>Contains one immutable screen frame produced by a terminal command.</summary>
public sealed record TerminalScreenFrame(
    IReadOnlyList<string> Lines,
    TerminalCursor Cursor,
    string? AccessibleLabel = null);

/// <summary>
/// Optional alternate-screen boundary supplied by a terminal renderer. Commands remain
/// compatible with ordinary streams when this service is absent.
/// </summary>
public interface IFullScreenTerminalSession
{
    /// <summary>Gets the latest renderer viewport.</summary>
    TerminalViewport Viewport { get; }

    /// <summary>Enters the alternate screen and transfers key ownership to the command.</summary>
    ValueTask EnterAlternateScreenAsync(CancellationToken cancellationToken = default);

    /// <summary>Atomically replaces the visible character-cell frame.</summary>
    ValueTask RenderAsync(TerminalScreenFrame frame, CancellationToken cancellationToken = default);

    /// <summary>Waits for the next key or resize event.</summary>
    ValueTask<TerminalKeyEvent> ReadKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>Restores the ordinary scrollback screen and input ownership.</summary>
    ValueTask LeaveAlternateScreenAsync(CancellationToken cancellationToken = default);
}
