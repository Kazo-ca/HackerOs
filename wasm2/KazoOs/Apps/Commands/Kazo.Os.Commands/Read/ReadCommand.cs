using System.Globalization;
using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Read;

/// <summary>
/// <c>read [-r] [-p prompt] [-s] [-n count] [-t seconds] [-d delim] [name...]</c> (ADR 0064):
/// reads one line of standard input and assigns its IFS-separated fields to the names (the last
/// name gets the rest of the line; <c>REPLY</c> without names). Without <c>-r</c> a backslash
/// escapes the next character and a trailing backslash continues the line. It sets variables of
/// the shell running it through <see cref="IShellSession.AssignVariable"/> (capability
/// <c>shell.control</c>), so inside a pipe (<c>echo x | read v</c>) the change stays in the
/// stage's subshell, exactly as in bash. Exit status 1 at end of input or on timeout.
/// </summary>
public sealed class ReadCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        if (context.Shell is not { } shell)
        {
            await context.StandardError.WriteLineAsync(text["NoShell"]);
            return 1;
        }

        bool raw = false;
        string? prompt = null;
        int? count = null;
        double? timeout = null;
        char delimiter = '\n';
        List<string> names = [];
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-r":
                    raw = true;
                    break;
                case "-s" or "-e":
                    break;
                case "-p" when i + 1 < args.Count:
                    prompt = args[++i];
                    break;
                case "-n" or "-N" when i + 1 < args.Count && int.TryParse(args[i + 1], out int n):
                    count = n;
                    i++;
                    break;
                case "-t" when i + 1 < args.Count && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double t):
                    timeout = t;
                    i++;
                    break;
                case "-d" when i + 1 < args.Count:
                    delimiter = args[++i] is { Length: > 0 } d ? d[0] : '\0';
                    break;
                default:
                    if (arg.StartsWith('-') && arg.Length > 1)
                    {
                        await context.StandardError.WriteLineAsync(text["InvalidOption", arg]);
                        await context.StandardError.WriteLineAsync(text["Usage"]);
                        return 2;
                    }

                    names.Add(arg);
                    break;
            }
        }

        foreach (string name in names)
        {
            if (!IsName(name))
            {
                await context.StandardError.WriteLineAsync(text["InvalidIdentifier", name]);
                return 1;
            }
        }

        if (prompt is not null)
        {
            // bash prints the prompt on standard error, so `read -p x v > file` keeps the file clean.
            await context.StandardError.WriteAsync(prompt);
            await context.StandardError.FlushAsync(cancellationToken);
        }

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } seconds)
        {
            limit.CancelAfter(TimeSpan.FromSeconds(seconds));
        }

        (string? line, bool complete) result;
        try
        {
            result = await ReadRecordAsync(context.StandardInput, delimiter, count, raw, limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return 142; // 128 + SIGALRM, bash's status for a timed-out read.
        }

        if (result.line is null)
        {
            return 1;
        }

        Assign(shell, names, result.line, shell.GetVariable("IFS") ?? " \t\n");
        return result.complete ? 0 : 1;
    }

    /// <summary>Reads up to the delimiter (or <paramref name="count"/> characters), handling backslashes unless raw.</summary>
    private static async Task<(string? Line, bool Complete)> ReadRecordAsync(
        TextReader input, char delimiter, int? count, bool raw, CancellationToken cancellationToken)
    {
        StringBuilder text = new();
        char[] buffer = new char[1];
        bool any = false;
        while (count is null || text.Length < count)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                return (any ? text.ToString() : null, false);
            }

            any = true;
            char ch = buffer[0];
            if (!raw && ch == '\\')
            {
                int next = await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
                if (next == 0)
                {
                    break;
                }

                if (buffer[0] != '\n')
                {
                    // An escaped character is kept literally, marked so field splitting skips it.
                    text.Append('\u0001').Append(buffer[0]);
                }

                continue;
            }

            if (ch == delimiter)
            {
                return (text.ToString(), true);
            }

            if (ch == '\r' && delimiter == '\n')
            {
                continue;
            }

            text.Append(ch);
        }

        return (text.ToString(), true);
    }

    /// <summary>Splits on IFS and assigns: one field per name, the last name taking the remainder.</summary>
    private static void Assign(IShellSession shell, List<string> names, string line, string ifs)
    {
        if (names.Count == 0)
        {
            shell.AssignVariable("REPLY", Unmark(line));
            return;
        }

        string whitespace = new([.. ifs.Where(ch => ch is ' ' or '\t' or '\n')]);
        int position = 0;
        SkipWhitespace();
        for (int index = 0; index < names.Count; index++)
        {
            if (index == names.Count - 1)
            {
                string rest = line[Math.Min(position, line.Length)..].TrimEnd(whitespace.ToCharArray());
                shell.AssignVariable(names[index], Unmark(rest));
                return;
            }

            int start = position;
            while (position < line.Length && !IsSeparator(position))
            {
                position += line[position] == '\u0001' ? 2 : 1;
            }

            shell.AssignVariable(names[index], Unmark(line[start..Math.Min(position, line.Length)]));
            if (position < line.Length && !char.IsWhiteSpace(line[position]))
            {
                position++;
            }

            SkipWhitespace();
        }

        void SkipWhitespace()
        {
            while (position < line.Length && whitespace.Contains(line[position]))
            {
                position++;
            }
        }

        bool IsSeparator(int at) => line[at] != '\u0001' && ifs.Contains(line[at]);
    }

    private static string Unmark(string text) => text.Replace("\u0001", string.Empty, StringComparison.Ordinal);

    private static bool IsName(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');
}
