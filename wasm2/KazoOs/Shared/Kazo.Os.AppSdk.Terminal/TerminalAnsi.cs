using System.Text;
using System.Text.RegularExpressions;

namespace Kazo.Os.AppSdk.Terminal;

/// <summary>
/// The ECMA-48 bytes <see cref="TerminalPanel"/> writes to its terminal emulator (ADR 0061):
/// the coloured prompt, newline translation of command output, and the alternate-screen frames
/// of full-screen commands.
/// </summary>
internal static partial class TerminalAnsi
{
    /// <summary>Control Sequence Introducer.</summary>
    public const string Csi = "\x1b[";

    /// <summary>Resets every graphic attribute.</summary>
    public const string Reset = Csi + "0m";

    /// <summary>Bold bright green, the colour bash's default Debian prompt gives user@host.</summary>
    public const string PromptUser = Csi + "1;32m";

    /// <summary>Bold blue, the colour of the prompt's directory.</summary>
    public const string PromptDirectory = Csi + "1;34m";

    /// <summary>Red, for errors the terminal itself reports.</summary>
    public const string Error = Csi + "31m";

    /// <summary>Yellow, for notices the terminal or the OS writes (approval banners, ^C).</summary>
    public const string Notice = Csi + "33m";

    /// <summary>Green, for the welcome banner.</summary>
    public const string Banner = Csi + "32m";

    /// <summary>Erases the screen and the scrollback and homes the cursor, as <c>clear</c> does.</summary>
    public const string ClearScreen = Csi + "H" + Csi + "2J" + Csi + "3J";

    /// <summary>Switches to the alternate screen and clears it (smcup).</summary>
    public const string EnterAlternateScreen = Csi + "?1049h" + Csi + "H" + Csi + "2J";

    /// <summary>Returns to the normal screen, with its content and cursor as they were (rmcup).</summary>
    public const string LeaveAlternateScreen = Csi + "?1049l" + Csi + "?25h";

    /// <summary>
    /// Formats a <c>user@host:dir$</c> prompt in bash's colours and returns its width in cells
    /// (the trailing space included). Any other prompt shape is printed as is.
    /// </summary>
    public static (string Text, int Width) FormatPrompt(string prompt)
    {
        prompt ??= string.Empty;
        string plain = prompt + " ";
        int at = prompt.IndexOf('@');
        int colon = at < 0 ? -1 : prompt.IndexOf(':', at);
        if (at <= 0 || colon < 0 || prompt.Length < 2)
        {
            return (plain, plain.Length);
        }

        char last = prompt[^1];
        bool hasSigil = last is '$' or '#';
        string identity = prompt[..colon];
        string directory = prompt[(colon + 1)..(hasSigil ? prompt.Length - 1 : prompt.Length)];
        string sigil = hasSigil ? last.ToString() : string.Empty;
        string text = PromptUser + identity + Reset + ":" + PromptDirectory + directory + Reset + sigil + " ";
        return (text, plain.Length);
    }

    /// <summary>
    /// Translates a command's output for the terminal the way a tty's <c>ONLCR</c> flag does:
    /// each <c>\n</c> becomes <c>\r\n</c>, so the next line starts in the first column.
    /// </summary>
    public static string TranslateOutput(string text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    /// <summary>
    /// Tells whether the terminal cursor is in the first column of a row once
    /// <paramref name="translated"/> (already ONLCR-translated) is written: after a line break or
    /// a cursor-addressing sequence. Text made only of other control sequences (colours) keeps
    /// <paramref name="previous"/>. Used to start the prompt on a fresh row.
    /// </summary>
    public static bool EndsAtLineStart(string translated, bool previous)
    {
        if (string.IsNullOrEmpty(translated))
        {
            return previous;
        }

        Match trailing = TrailingSequences().Match(translated);
        string text = trailing.Success ? translated[..trailing.Index] : translated;
        if (trailing.Success && trailing.Value.Contains('H'))
        {
            return true;
        }

        return text.Length == 0 ? previous : text[^1] is '\n' or '\r';
    }

    [GeneratedRegex(@"(?:\x1b\[[0-?]*[ -/]*[@-~])+$")]
    private static partial Regex TrailingSequences();

    /// <summary>
    /// Removes what a person would not read: CSI sequences (colours, cursor moves), OSC sequences
    /// (titles), other escapes, carriage returns and the remaining C0 controls but tab and newline.
    /// Used for the text an embedded terminal reports to its host (<c>IKazoTerminal</c>, ADR 0063).
    /// </summary>
    public static string StripControlSequences(string text) =>
        string.IsNullOrEmpty(text) ? string.Empty : ControlSequences().Replace(text, string.Empty);

    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)?|\x1b[@-Z\\-_]|[\x00-\x08\x0b-\x1f\x7f]")]
    private static partial Regex ControlSequences();

    /// <summary>
    /// Draws a full-screen command's frame on the alternate screen: each row is written in
    /// place and cleared to its end, rows the frame does not cover are erased, then the cursor
    /// is placed (or hidden). Rows longer than the viewport are cut, so nothing wraps.
    /// </summary>
    public static string RenderFrame(TerminalScreenFrame frame, int columns, int rows)
    {
        ArgumentNullException.ThrowIfNull(frame);
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        StringBuilder output = new();
        output.Append(Csi).Append("?25l");
        int lineCount = Math.Min(frame.Lines.Count, rows);
        for (int row = 0; row < lineCount; row++)
        {
            string line = (frame.Lines[row] ?? string.Empty).Replace('\t', ' '); // one cell, as the frame's cursor column counts it
            (line, int width, bool styled) = FitToColumns(line, columns);
            output.Append(Csi).Append(row + 1).Append(";1H").Append(line);
            if (styled)
            {
                // A coloured row (htop) must not bleed its attributes into the next one.
                output.Append(Csi).Append("0m");
            }

            if (width < columns)
            {
                // Not after a full row: with the cursor parked on the last cell, EL would erase it.
                output.Append(Csi).Append('K');
            }
        }

        if (lineCount < rows)
        {
            output.Append(Csi).Append(lineCount + 1).Append(";1H").Append(Csi).Append('J');
        }

        TerminalCursor cursor = frame.Cursor;
        int cursorRow = Math.Clamp(cursor.Row, 0, rows - 1);
        int cursorColumn = Math.Clamp(cursor.Column, 0, columns - 1);
        output.Append(Csi).Append(cursorRow + 1).Append(';').Append(cursorColumn + 1).Append('H');
        if (cursor.Visible)
        {
            output.Append(Csi).Append("?25h");
        }

        return output.ToString();
    }

    /// <summary>
    /// Cuts a frame row to <paramref name="columns"/> visible cells, keeping its SGR and other
    /// CSI sequences whole and uncounted (ADR 0064: full-screen commands such as <c>htop</c> colour
    /// their rows).
    /// </summary>
    /// <returns>The row, its visible width, and whether it holds escape sequences.</returns>
    internal static (string Line, int Width, bool Styled) FitToColumns(string line, int columns)
    {
        if (line.IndexOf('\x1b') < 0)
        {
            return line.Length > columns ? (line[..columns], columns, false) : (line, line.Length, false);
        }

        StringBuilder fitted = new(line.Length);
        int width = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '\x1b' && i + 1 < line.Length && line[i + 1] == '[')
            {
                int end = i + 2;
                while (end < line.Length && line[end] is < '@' or > '~')
                {
                    end++;
                }

                fitted.Append(line, i, Math.Min(end + 1, line.Length) - i);
                i = end;
                continue;
            }

            if (width == columns)
            {
                continue;
            }

            fitted.Append(ch);
            width++;
        }

        return (fitted.ToString(), width, true);
    }
}
