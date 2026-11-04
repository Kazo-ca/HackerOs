using System.Globalization;
using System.Text.RegularExpressions;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Common;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Sort;

/// <summary>
/// Implements the <c>sort</c> terminal command (`P4-W5-CMD-003`, options extended by ADR 0064):
/// sorts the lines of the files or standard input, like coreutils: <c>-r</c> reverse,
/// <c>-n</c> numeric (leading number), <c>-h</c> human sizes (<c>2K</c> &lt; <c>1M</c>),
/// <c>-V</c> versions, <c>-f</c> ignore case, <c>-b</c> ignore leading blanks, <c>-u</c>
/// unique, <c>-k N[,M]</c> keys with <c>-t SEP</c>, <c>-o FILE</c> output, flags combinable
/// (<c>-rn</c>). The sort is stable with a last-resort byte comparison, as GNU sort.
/// </summary>
public sealed class SortCommand : TerminalAppBase
{
    /// <summary>Initializes the command with its manifest.</summary>
    public SortCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> messages = context.App.GetLocalizer<Strings>();

        bool reverse = false, numeric = false, human = false, version = false, ignoreCase = false, blanks = false, unique = false, check = false;
        char? separator = null;
        (int Start, int End)? key = null;
        string? output = null;
        List<string> files = [];
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--reverse": reverse = true; continue;
                case "--numeric-sort": numeric = true; continue;
                case "--unique": unique = true; continue;
                case "--ignore-case": ignoreCase = true; continue;
                case "--human-numeric-sort": human = true; continue;
                case "--version-sort": version = true; continue;
                case "-k" or "--key" when i + 1 < args.Count: key = ParseKey(args[++i]); continue;
                case "-t" or "--field-separator" when i + 1 < args.Count: separator = args[++i].FirstOrDefault('\t'); continue;
                case "-o" or "--output" when i + 1 < args.Count: output = args[++i]; continue;
                case "-" or "--": files.Add(a == "-" ? "-" : string.Empty); continue;
            }

            if (a.StartsWith("-k", StringComparison.Ordinal) && a.Length > 2) { key = ParseKey(a[2..]); continue; }
            if (a.StartsWith("-t", StringComparison.Ordinal) && a.Length > 2) { separator = a[2]; continue; }
            if (a.Length > 1 && a[0] == '-' && !a.StartsWith("--", StringComparison.Ordinal))
            {
                foreach (char flag in a[1..])
                {
                    switch (flag)
                    {
                        case 'r': reverse = true; break;
                        case 'n': numeric = true; break;
                        case 'u': unique = true; break;
                        case 'f': ignoreCase = true; break;
                        case 'h': human = true; break;
                        case 'V': version = true; break;
                        case 'b': blanks = true; break;
                        case 'c' or 'C': check = true; break;
                        case 'g': numeric = true; break;
                        case 's' or 'd' or 'i' or 'M' or 'R': break;
                        default:
                            context.StandardError.WriteLine(messages["InvalidOption", flag]);
                            return 2;
                    }
                }

                continue;
            }

            files.Add(a);
        }

        files.RemoveAll(f => f.Length == 0);
        List<string> lines = [];
        int status = 0;
        foreach (string file in files.Count == 0 ? ["-"] : files)
        {
            string? text = await CommandIO.ReadTextAsync(context, file, "sort", cancellationToken);
            if (text is null)
            {
                status = 2;
                continue;
            }

            lines.AddRange(CommandIO.SplitLines(text));
        }

        StringComparison textComparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int Compare(string x, string y)
        {
            string kx = Key(x), ky = Key(y);
            int result = numeric ? LeadingNumber(kx).CompareTo(LeadingNumber(ky))
                : human ? HumanNumber(kx).CompareTo(HumanNumber(ky))
                : version ? CompareVersions(kx, ky)
                : string.Compare(kx, ky, textComparison);
            if (result == 0 && !unique)
            {
                // GNU sort's last-resort comparison: the whole lines, byte by byte.
                result = string.CompareOrdinal(x, y);
            }

            return reverse ? -result : result;
        }

        string Key(string line)
        {
            string value = line;
            if (key is { } k)
            {
                string[] fields = separator is { } sep ? line.Split(sep) : Regex.Split(line.Trim(), @"\s+");
                int start = Math.Min(k.Start - 1, fields.Length);
                int end = Math.Min(k.End == 0 ? fields.Length : k.End, fields.Length);
                value = start < end ? string.Join(separator?.ToString() ?? " ", fields[start..end]) : string.Empty;
            }

            return blanks ? value.TrimStart() : value;
        }

        if (check)
        {
            for (int index = 1; index < lines.Count; index++)
            {
                if (Compare(lines[index - 1], lines[index]) > 0)
                {
                    context.StandardError.WriteLine(messages["Disorder", index + 1, lines[index]]);
                    return 1;
                }
            }

            return 0;
        }

        List<string> sorted = [.. lines.Order(Comparer<string>.Create(Compare))];
        if (unique)
        {
            List<string> distinct = [];
            foreach (string line in sorted)
            {
                if (distinct.Count == 0 || Compare(distinct[^1], line) != 0)
                {
                    distinct.Add(line);
                }
            }

            sorted = distinct;
        }

        if (output is not null)
        {
            string resolved = CommandIO.ResolvePath(context.WorkingDirectory, output);
            try
            {
                await AppSdk.IO.HackFile.WriteAllLinesAsync(resolved, sorted, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                context.StandardError.WriteLine(messages["OpenFailed", output, CommandText.Describe(context, exception)]);
                return 2;
            }

            return status;
        }

        foreach (string line in sorted)
        {
            await context.StandardOutput.WriteLineAsync(line);
        }

        return status;
    }

    /// <summary><c>N</c>, <c>N,M</c>, ignoring character offsets and ordering letters (<c>2n</c>).</summary>
    private static (int, int) ParseKey(string text)
    {
        string[] parts = text.Split(',');
        static int Field(string part) =>
            int.TryParse(new string([.. part.Split('.')[0].TakeWhile(char.IsAsciiDigit)]), out int n) ? Math.Max(1, n) : 1;
        return (Field(parts[0]), parts.Length > 1 ? Field(parts[1]) : 0);
    }

    /// <summary>The number a line starts with (blanks skipped), 0 when none, as <c>sort -n</c> reads it.</summary>
    private static double LeadingNumber(string text)
    {
        Match match = Regex.Match(text, @"^\s*[-+]?(\d+(\.\d*)?|\.\d+)");
        return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
    }

    /// <summary><c>sort -h</c>: a number with an optional K/M/G/T/P suffix.</summary>
    private static double HumanNumber(string text)
    {
        Match match = Regex.Match(text, @"^\s*([-+]?\d+(\.\d+)?)\s*([KMGTPE]?)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return 0;
        }

        double value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int power = "KMGTPE".IndexOf(char.ToUpperInvariant(match.Groups[3].Value.FirstOrDefault('\0'))) + 1;
        return value * Math.Pow(1024, power);
    }

    /// <summary><c>sort -V</c>: digit runs compare as numbers, the rest as text.</summary>
    private static int CompareVersions(string x, string y)
    {
        string[] a = Regex.Split(x, @"(\d+)");
        string[] b = Regex.Split(y, @"(\d+)");
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            int result = long.TryParse(a[i], out long na) && long.TryParse(b[i], out long nb)
                ? na.CompareTo(nb)
                : string.CompareOrdinal(a[i], b[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return a.Length.CompareTo(b.Length);
    }
}
