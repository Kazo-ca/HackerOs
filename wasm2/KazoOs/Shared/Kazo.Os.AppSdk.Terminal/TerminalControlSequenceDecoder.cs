using System.Text;

namespace Kazo.Os.AppSdk.Terminal;

/// <summary>What one decoded piece of a command's output asks the terminal to do.</summary>
public enum TerminalOutputActionKind
{
    /// <summary>Print <see cref="TerminalOutputAction.Text"/>.</summary>
    Text,

    /// <summary>Erase the screen and its scrollback (<c>ESC[2J</c> or <c>ESC[3J</c>).</summary>
    EraseScreen
}

/// <summary>One decoded piece of output: text to print, or a screen operation.</summary>
/// <param name="Kind">The operation.</param>
/// <param name="Text">The text, for <see cref="TerminalOutputActionKind.Text"/>; empty otherwise.</param>
public sealed record TerminalOutputAction(TerminalOutputActionKind Kind, string Text);

/// <summary>
/// Interprets the ECMA-48 control sequences a command writes to the terminal (ADR 0051,
/// section 4), the way a real terminal emulator does rather than the shell: <c>clear</c> is just a
/// command that writes <c>ESC[H ESC[2J ESC[3J</c>. Erase-display sequences become
/// <see cref="TerminalOutputActionKind.EraseScreen"/>; cursor moves and every other CSI sequence
/// (colours included, for now) are consumed silently so they never show as garbage.
/// </summary>
/// <remarks>
/// Output arrives in chunks of any size (ADR 0050, section 4), so a sequence cut between two
/// writes is kept and completed by the next <see cref="Decode"/> call. One decoder per stream.
/// </remarks>
public sealed class TerminalControlSequenceDecoder
{
    private const char Escape = '\x1b';

    /// <summary>Longest sequence buffered while incomplete; beyond it the bytes are dropped as garbage.</summary>
    private const int MaxPendingLength = 64;

    private readonly StringBuilder _pending = new();

    /// <summary>Decodes one chunk of output, together with any sequence left incomplete by the previous chunk.</summary>
    /// <param name="chunk">Text written by a command.</param>
    /// <returns>The text runs and screen operations, in order.</returns>
    public IReadOnlyList<TerminalOutputAction> Decode(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        string input = _pending.Length == 0 ? chunk : _pending + chunk;
        _pending.Clear();

        List<TerminalOutputAction> actions = [];
        StringBuilder text = new();
        int index = 0;
        while (index < input.Length)
        {
            char current = input[index];
            if (current != Escape)
            {
                text.Append(current);
                index++;
                continue;
            }

            if (index + 1 >= input.Length)
            {
                Keep(input, index);
                break;
            }

            if (input[index + 1] != '[')
            {
                // A lone ESC or a non-CSI escape (e.g. ESC c): two characters, ignored.
                index += 2;
                continue;
            }

            // CSI: parameter and intermediate bytes (0x20-0x3F) up to a final byte (0x40-0x7E).
            int end = index + 2;
            while (end < input.Length && input[end] is >= '\x20' and <= '\x3f')
            {
                end++;
            }

            if (end >= input.Length)
            {
                Keep(input, index);
                break;
            }

            string parameters = input[(index + 2)..end];
            char final = input[end];
            if (final == 'J' && parameters is "2" or "3")
            {
                Flush(text, actions);
                if (actions.Count == 0 || actions[^1].Kind != TerminalOutputActionKind.EraseScreen)
                {
                    actions.Add(new TerminalOutputAction(TerminalOutputActionKind.EraseScreen, string.Empty));
                }
            }

            index = end + 1;
        }

        Flush(text, actions);
        return actions;
    }

    private void Keep(string input, int start)
    {
        if (input.Length - start <= MaxPendingLength)
        {
            _pending.Append(input, start, input.Length - start);
        }
    }

    private static void Flush(StringBuilder text, List<TerminalOutputAction> actions)
    {
        if (text.Length > 0)
        {
            actions.Add(new TerminalOutputAction(TerminalOutputActionKind.Text, text.ToString()));
            text.Clear();
        }
    }
}
