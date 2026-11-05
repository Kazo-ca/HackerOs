using System.Text;

namespace Kazo.Os.AppSdk.Terminal;

/// <summary>
/// The line being typed at the prompt, edited the way readline does in a real shell (cursor
/// moves, insertion, deletion, Ctrl+U/K/W), and the control sequences that redraw it on a
/// character-cell terminal (ADR 0061).
/// </summary>
/// <remarks>
/// The editor never prints the screen itself: <see cref="Render"/> returns the bytes to write.
/// It remembers on which row of the prompt's block the terminal cursor was left, so a redraw
/// can go back to the prompt's first row even when the line wraps over several rows. Every
/// character is assumed to be one cell wide.
/// </remarks>
internal sealed class TerminalLineEditor
{
    private const string Esc = "\x1b";
    private readonly StringBuilder _buffer = new();
    private int _cursorRow;

    /// <summary>Gets the text typed so far.</summary>
    public string Text => _buffer.ToString();

    /// <summary>Gets the cursor position inside <see cref="Text"/> (0 = before the first character).</summary>
    public int Cursor { get; private set; }

    /// <summary>Gets whether nothing is typed.</summary>
    public bool IsEmpty => _buffer.Length == 0;

    /// <summary>Inserts text at the cursor and moves the cursor after it.</summary>
    public void Insert(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _buffer.Insert(Cursor, text);
        Cursor += text.Length;
    }

    /// <summary>Deletes the character before the cursor (Backspace). Returns false at the start of the line.</summary>
    public bool Backspace()
    {
        if (Cursor == 0)
        {
            return false;
        }

        _buffer.Remove(Cursor - 1, 1);
        Cursor--;
        return true;
    }

    /// <summary>Deletes the character under the cursor (Delete). Returns false at the end of the line.</summary>
    public bool Delete()
    {
        if (Cursor >= _buffer.Length)
        {
            return false;
        }

        _buffer.Remove(Cursor, 1);
        return true;
    }

    /// <summary>Moves the cursor by <paramref name="delta"/> characters, within the line.</summary>
    public bool Move(int delta)
    {
        int next = Math.Clamp(Cursor + delta, 0, _buffer.Length);
        bool moved = next != Cursor;
        Cursor = next;
        return moved;
    }

    /// <summary>Moves the cursor to the start of the line (Home, Ctrl+A).</summary>
    public bool MoveToStart() => Move(-Cursor);

    /// <summary>Moves the cursor to the end of the line (End, Ctrl+E).</summary>
    public bool MoveToEnd() => Move(_buffer.Length - Cursor);

    /// <summary>Deletes from the start of the line to the cursor (Ctrl+U).</summary>
    public bool KillToStart()
    {
        if (Cursor == 0)
        {
            return false;
        }

        _buffer.Remove(0, Cursor);
        Cursor = 0;
        return true;
    }

    /// <summary>Deletes from the cursor to the end of the line (Ctrl+K).</summary>
    public bool KillToEnd()
    {
        if (Cursor >= _buffer.Length)
        {
            return false;
        }

        _buffer.Remove(Cursor, _buffer.Length - Cursor);
        return true;
    }

    /// <summary>Deletes the word before the cursor, and the blanks after it (Ctrl+W).</summary>
    public bool DeleteWordBefore()
    {
        int start = Cursor;
        while (start > 0 && char.IsWhiteSpace(_buffer[start - 1]))
        {
            start--;
        }

        while (start > 0 && !char.IsWhiteSpace(_buffer[start - 1]))
        {
            start--;
        }

        if (start == Cursor)
        {
            return false;
        }

        _buffer.Remove(start, Cursor - start);
        Cursor = start;
        return true;
    }

    /// <summary>Replaces the whole line (history recall, completion) and puts the cursor at <paramref name="cursor"/>, or at the end.</summary>
    public void SetText(string text, int? cursor = null)
    {
        _buffer.Clear().Append(text ?? string.Empty);
        Cursor = Math.Clamp(cursor ?? _buffer.Length, 0, _buffer.Length);
    }

    /// <summary>
    /// Starts a new, empty line whose prompt is about to be printed at the start of a terminal
    /// row: forgets where the previous line left the cursor.
    /// </summary>
    public void Reset()
    {
        _buffer.Clear();
        Cursor = 0;
        _cursorRow = 0;
    }

    /// <summary>The screen was cleared and the cursor homed: the prompt is redrawn on the first row.</summary>
    public void HomeCursor() => _cursorRow = 0;

    /// <summary>
    /// Keeps the remembered cursor row right after the terminal reflowed to a new width (the
    /// terminal rewraps the prompt's block, so the cursor's row inside it changes).
    /// </summary>
    public void Reflow(int promptWidth, int columns)
    {
        _cursorRow = (promptWidth + Cursor) / Math.Max(1, columns);
    }

    /// <summary>
    /// Returns the bytes that redraw the prompt and the line in place and leave the terminal
    /// cursor at <see cref="Cursor"/>: back to the block's first row, erase to the end of the
    /// screen, print, then move the cursor back from the end.
    /// </summary>
    /// <param name="prompt">The prompt as printed (it may contain colour sequences).</param>
    /// <param name="promptWidth">How many cells the prompt occupies.</param>
    /// <param name="columns">The terminal's width in cells.</param>
    public string Render(string prompt, int promptWidth, int columns)
    {
        columns = Math.Max(1, columns);
        StringBuilder output = new();
        if (_cursorRow > 0)
        {
            output.Append(Esc).Append('[').Append(_cursorRow).Append('A');
        }

        output.Append('\r').Append(Esc).Append("[J").Append(prompt).Append(_buffer);

        int end = promptWidth + _buffer.Length;
        if (end > 0 && end % columns == 0)
        {
            // The last cell of a row was just written and the terminal waits to wrap: print a
            // blank and step back so the cursor really sits at the start of the next row.
            output.Append(" \b");
        }

        int endRow = end / columns;
        int target = promptWidth + Cursor;
        int targetRow = target / columns;
        int targetColumn = target % columns;
        if (endRow > targetRow)
        {
            output.Append(Esc).Append('[').Append(endRow - targetRow).Append('A');
        }

        output.Append('\r');
        if (targetColumn > 0)
        {
            output.Append(Esc).Append('[').Append(targetColumn).Append('C');
        }

        _cursorRow = targetRow;
        return output.ToString();
    }

    /// <summary>
    /// Returns the bytes that move the terminal cursor below the line, as Enter does before the
    /// command runs, and starts a new empty line.
    /// </summary>
    public string Commit(int promptWidth, int columns)
    {
        columns = Math.Max(1, columns);
        int end = promptWidth + _buffer.Length;
        int endRow = end / columns;
        int rowsDown = endRow - _cursorRow;
        string output = (rowsDown > 0 ? $"{Esc}[{rowsDown}B" : string.Empty) + "\r\n";
        Reset();
        return output;
    }
}
