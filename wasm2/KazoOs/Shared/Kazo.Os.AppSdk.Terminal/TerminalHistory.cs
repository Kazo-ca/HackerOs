namespace Kazo.Os.AppSdk.Terminal;

/// <summary>
/// Folds a command typed over several lines (PS2 continuations, ADR 0064) into one history
/// entry, like bash's <c>cmdhist</c>: a line ending with a word or operator that expects more
/// (<c>do</c>, <c>then</c>, <c>|</c>, <c>{</c>…) is joined with a space, any other with
/// <c>; </c>. The line editor then recalls it as one editable line.
/// </summary>
public static class TerminalHistory
{
    private static readonly string[] OpenWords = ["do", "then", "else", "in"];
    private static readonly string[] OpenOperators = ["{", "(", "|", "&&", "||", ";"];

    /// <summary>Joins the lines of <paramref name="text"/> into one command line.</summary>
    public static string ToSingleLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = text.Split('\n');
        if (lines.Length == 1)
        {
            return text;
        }

        string joined = lines[0].TrimEnd();
        foreach (string raw in lines.Skip(1))
        {
            string next = raw.Trim();
            if (next.Length == 0)
            {
                continue;
            }

            joined = joined.Length == 0 || ExpectsMore(joined) ? $"{joined} {next}".TrimStart() : $"{joined}; {next}";
        }

        return joined;
    }

    private static bool ExpectsMore(string line)
    {
        if (OpenOperators.Any(op => line.EndsWith(op, StringComparison.Ordinal)))
        {
            return true;
        }

        string lastWord = line[(line.LastIndexOfAny([' ', '\t', ';']) + 1)..];
        return OpenWords.Contains(lastWord, StringComparer.Ordinal);
    }
}
