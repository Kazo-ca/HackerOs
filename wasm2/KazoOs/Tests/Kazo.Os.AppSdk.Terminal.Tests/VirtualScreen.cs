using System.Text;

namespace Kazo.Os.AppSdk.Terminal.Tests;

/// <summary>
/// A minimal character-cell terminal for tests: printable characters with xterm's deferred
/// wrap, CR, LF, BS, and the CSI sequences TerminalLineEditor and TerminalAnsi write
/// (A, B, C, D, H, J, K; other sequences are ignored). Enough to check where text and cursor end.
/// </summary>
internal sealed class VirtualScreen(int columns, int rows)
{
    private readonly List<char[]> _lines = [.. Enumerable.Range(0, rows).Select(_ => NewRow(columns))];
    private bool _wrapPending;

    public int Row { get; private set; }

    public int Column { get; private set; }

    public string Line(int row) => new string(_lines[row]).TrimEnd();

    public void Write(string data)
    {
        for (int index = 0; index < data.Length; index++)
        {
            char current = data[index];
            if (current == '\x1b' && index + 1 < data.Length && data[index + 1] == '[')
            {
                int end = index + 2;
                while (end < data.Length && !(data[end] >= '@' && data[end] <= '~'))
                {
                    end++;
                }

                ApplyCsi(data[(index + 2)..end], data[end]);
                index = end;
                continue;
            }

            switch (current)
            {
                case '\r':
                    Column = 0;
                    _wrapPending = false;
                    break;
                case '\n':
                    LineFeed();
                    _wrapPending = false;
                    break;
                case '\b':
                    if (_wrapPending)
                        _wrapPending = false;
                    else
                        Column = Math.Max(0, Column - 1);
                    break;
                default:
                    Print(current);
                    break;
            }
        }
    }

    private void Print(char value)
    {
        if (_wrapPending)
        {
            Column = 0;
            LineFeed();
            _wrapPending = false;
        }

        _lines[Row][Column] = value;
        if (Column == columns - 1)
            _wrapPending = true;
        else
            Column++;
    }

    private void LineFeed()
    {
        if (Row == rows - 1)
        {
            _lines.RemoveAt(0);
            _lines.Add(NewRow(columns));
        }
        else
        {
            Row++;
        }
    }

    private void ApplyCsi(string parameters, char final)
    {
        if (parameters.StartsWith('?'))
            return;
        string[] parts = parameters.Split(';');
        int First(int fallback) => int.TryParse(parts[0], out int value) ? value : fallback;
        _wrapPending = false;
        switch (final)
        {
            case 'A': Row = Math.Max(0, Row - First(1)); break;
            case 'B': Row = Math.Min(rows - 1, Row + First(1)); break;
            case 'C': Column = Math.Min(columns - 1, Column + First(1)); break;
            case 'D': Column = Math.Max(0, Column - First(1)); break;
            case 'H':
                Row = Math.Clamp(First(1) - 1, 0, rows - 1);
                Column = parts.Length > 1 && int.TryParse(parts[1], out int column) ? Math.Clamp(column - 1, 0, columns - 1) : 0;
                break;
            case 'K':
                for (int c = Column; c < columns; c++) _lines[Row][c] = ' ';
                break;
            case 'J':
                for (int c = Column; c < columns; c++) _lines[Row][c] = ' ';
                for (int r = Row + 1; r < rows; r++) _lines[r] = NewRow(columns);
                break;
        }
    }

    private static char[] NewRow(int columns)
    {
        char[] row = new char[columns];
        Array.Fill(row, ' ');
        return row;
    }

    public override string ToString()
    {
        StringBuilder text = new();
        for (int r = 0; r < rows; r++) text.AppendLine(Line(r));
        return text.ToString();
    }
}
