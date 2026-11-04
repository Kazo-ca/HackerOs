namespace Kazo.Os.AppSdk.Terminal;

/// <summary>
/// Turns the bytes a terminal emulator sends for the keyboard (xterm.js <c>onData</c>, ADR 0061)
/// back into <see cref="TerminalKeyEvent"/>s, as the terminal driver of a real system receives
/// <c>0x7f</c> for Backspace and <c>ESC [ A</c> for the up arrow.
/// </summary>
/// <remarks>
/// <para>
/// xterm.js delivers each key's whole sequence in one call, so a lone <c>ESC</c> at the end of a
/// chunk is the Escape key, never the start of a sequence cut in two. A paste arrives as one
/// chunk of text: every character becomes a key, and each newline an Enter.
/// </para>
/// <para>
/// Tab is reported as <see cref="TerminalKey.Character"/> with text <c>"\t"</c> (the contract
/// has no Tab member); Ctrl+letter as <see cref="TerminalKey.Character"/> with the lowercase
/// letter and <see cref="TerminalKeyEvent.Control"/> set; Alt+key (<c>ESC</c> prefix) with
/// <see cref="TerminalKeyEvent.Alt"/> set.
/// </para>
/// </remarks>
internal static class TerminalInputDecoder
{
    private const char Escape = '\x1b';

    /// <summary>Decodes one chunk of terminal input into the keys it represents, in order.</summary>
    /// <param name="data">The bytes the terminal emulator sent, as a string.</param>
    /// <returns>The decoded keys; unknown sequences are dropped.</returns>
    public static IReadOnlyList<TerminalKeyEvent> Decode(string? data)
    {
        List<TerminalKeyEvent> keys = [];
        if (string.IsNullOrEmpty(data))
        {
            return keys;
        }

        int index = 0;
        while (index < data.Length)
        {
            char current = data[index];
            if (current == Escape)
            {
                index = DecodeEscape(data, index, keys);
                continue;
            }

            if (current == '\r')
            {
                keys.Add(new TerminalKeyEvent(TerminalKey.Enter));
                // A pasted "\r\n" is one line break, not two.
                index += index + 1 < data.Length && data[index + 1] == '\n' ? 2 : 1;
                continue;
            }

            if (char.IsHighSurrogate(current) && index + 1 < data.Length && char.IsLowSurrogate(data[index + 1]))
            {
                keys.Add(new TerminalKeyEvent(TerminalKey.Character, data.Substring(index, 2)));
                index += 2;
                continue;
            }

            if (DecodeControl(current) is { } control)
            {
                keys.Add(control);
            }
            else if (!char.IsControl(current))
            {
                keys.Add(new TerminalKeyEvent(TerminalKey.Character, current.ToString()));
            }

            index++;
        }

        return keys;
    }

    /// <summary>Maps a C0 control byte (or DEL) to its key, or null when it has no meaning here.</summary>
    private static TerminalKeyEvent? DecodeControl(char value) => value switch
    {
        '\n' => new TerminalKeyEvent(TerminalKey.Enter),
        '\t' => new TerminalKeyEvent(TerminalKey.Character, "\t"),
        '\x7f' or '\b' => new TerminalKeyEvent(TerminalKey.Backspace),
        >= '\x01' and <= '\x1a' => new TerminalKeyEvent(
            TerminalKey.Character, ((char)('a' + value - 1)).ToString(), Control: true),
        _ => null
    };

    /// <summary>Decodes the sequence starting at the <c>ESC</c> at <paramref name="start"/>; returns the next index.</summary>
    private static int DecodeEscape(string data, int start, List<TerminalKeyEvent> keys)
    {
        int next = start + 1;
        if (next >= data.Length)
        {
            keys.Add(new TerminalKeyEvent(TerminalKey.Escape));
            return next;
        }

        char introducer = data[next];
        if (introducer == '[')
        {
            return DecodeCsi(data, next + 1, keys);
        }

        if (introducer == 'O' && next + 1 < data.Length)
        {
            // SS3: the cursor keys in application mode, and Home/End on some keyboards.
            if (FinalToKey(data[next + 1]) is { } ss3Key)
            {
                keys.Add(new TerminalKeyEvent(ss3Key));
            }

            return next + 2;
        }

        if (introducer == Escape)
        {
            keys.Add(new TerminalKeyEvent(TerminalKey.Escape));
            return next;
        }

        // Meta prefix: Alt+key.
        if (introducer == '\x7f')
        {
            keys.Add(new TerminalKeyEvent(TerminalKey.Backspace, Alt: true));
        }
        else if (!char.IsControl(introducer))
        {
            keys.Add(new TerminalKeyEvent(TerminalKey.Character, introducer.ToString(), Alt: true));
        }

        return next + 1;
    }

    /// <summary>Decodes a CSI sequence whose parameters start at <paramref name="start"/>; returns the next index.</summary>
    private static int DecodeCsi(string data, int start, List<TerminalKeyEvent> keys)
    {
        int index = start;
        while (index < data.Length && data[index] is >= '\x30' and <= '\x3f')
        {
            index++;
        }

        while (index < data.Length && data[index] is >= '\x20' and <= '\x2f')
        {
            index++;
        }

        if (index >= data.Length)
        {
            // Truncated sequence: nothing sensible to report.
            return index;
        }

        char final = data[index];
        string[] parameters = data[start..index].Split(';');
        // xterm encodes modifiers as 1 + (Shift=1 | Alt=2 | Ctrl=4) in the second parameter.
        int modifiers = parameters.Length > 1 && int.TryParse(parameters[1], out int encoded) ? encoded - 1 : 0;
        bool shift = (modifiers & 1) != 0;
        bool alt = (modifiers & 2) != 0;
        bool control = (modifiers & 4) != 0;

        TerminalKey? key = final == '~'
            ? TildeToKey(parameters[0])
            : FinalToKey(final);
        if (key is { } decoded)
        {
            keys.Add(new TerminalKeyEvent(decoded, Control: control, Alt: alt, Shift: shift));
        }
        else if (final == 'Z')
        {
            // Shift+Tab (back-tab).
            keys.Add(new TerminalKeyEvent(TerminalKey.Character, "\t", Shift: true));
        }

        return index + 1;
    }

    private static TerminalKey? FinalToKey(char final) => final switch
    {
        'A' => TerminalKey.ArrowUp,
        'B' => TerminalKey.ArrowDown,
        'C' => TerminalKey.ArrowRight,
        'D' => TerminalKey.ArrowLeft,
        'H' => TerminalKey.Home,
        'F' => TerminalKey.End,
        _ => null
    };

    private static TerminalKey? TildeToKey(string code) => code switch
    {
        "1" or "7" => TerminalKey.Home,
        "4" or "8" => TerminalKey.End,
        "3" => TerminalKey.Delete,
        "5" => TerminalKey.PageUp,
        "6" => TerminalKey.PageDown,
        _ => null
    };
}
