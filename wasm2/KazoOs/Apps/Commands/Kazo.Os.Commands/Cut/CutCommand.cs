using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Common;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Cut;

/// <summary>
/// <c>cut</c> (ADR 0064), as coreutils: <c>-f LIST</c> selects fields separated by <c>-d</c>
/// (TAB by default; <c>-s</c> drops lines without one, <c>--output-delimiter</c> changes the
/// joiner), <c>-c LIST</c> characters and <c>-b LIST</c> bytes (the same here: text is UTF-16).
/// A LIST is <c>N</c>, <c>N-M</c>, <c>N-</c>, <c>-M</c>, comma-separated; <c>--complement</c>
/// inverts it. Reads the files or standard input.
/// </summary>
public sealed class CutCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        string? fieldList = null, charList = null;
        string delimiter = "\t";
        string? outputDelimiter = null;
        bool onlyDelimited = false, complement = false;
        List<string> files = [];
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            string? Value(string option) =>
                arg.Length > option.Length ? arg[option.Length..] : i + 1 < args.Count ? args[++i] : null;

            if (arg.StartsWith("--output-delimiter=", StringComparison.Ordinal))
            {
                outputDelimiter = arg["--output-delimiter=".Length..];
            }
            else if (arg == "--complement")
            {
                complement = true;
            }
            else if (arg is "-s" or "--only-delimited")
            {
                onlyDelimited = true;
            }
            else if (arg.StartsWith("-f", StringComparison.Ordinal))
            {
                fieldList = Value("-f");
            }
            else if (arg.StartsWith("-c", StringComparison.Ordinal) || arg.StartsWith("-b", StringComparison.Ordinal))
            {
                charList = Value(arg[..2]);
            }
            else if (arg.StartsWith("-d", StringComparison.Ordinal))
            {
                delimiter = Value("-d") ?? "\t";
                if (delimiter.Length != 1)
                {
                    await context.StandardError.WriteLineAsync(text["DelimiterLength"]);
                    return 1;
                }
            }
            else if (arg.StartsWith('-') && arg.Length > 1)
            {
                await context.StandardError.WriteLineAsync(text["InvalidOption", arg.TrimStart('-')]);
                return 1;
            }
            else
            {
                files.Add(arg);
            }
        }

        if ((fieldList is null) == (charList is null))
        {
            await context.StandardError.WriteLineAsync(fieldList is null
                ? text["NoList"]
                : text["OneListOnly"]);
            await context.StandardError.WriteLineAsync(text["TryHelp"]);
            return 1;
        }

        List<(int From, int To)>? ranges = ParseList(fieldList ?? charList!);
        if (ranges is null)
        {
            await context.StandardError.WriteLineAsync(text["InvalidList", fieldList ?? charList ?? string.Empty]);
            return 1;
        }

        StrongBox<bool> failed = new(false);
        await foreach (string line in CommandIO.ReadLinesAsync(context, files, "cut", failed, cancellationToken))
        {
            if (charList is not null)
            {
                StringBuilder selected = new();
                for (int index = 1; index <= line.Length; index++)
                {
                    if (Selected(ranges, index) != complement)
                    {
                        selected.Append(line[index - 1]);
                    }
                }

                await context.StandardOutput.WriteLineAsync(selected.ToString());
                continue;
            }

            if (!line.Contains(delimiter[0]))
            {
                if (!onlyDelimited)
                {
                    await context.StandardOutput.WriteLineAsync(line);
                }

                continue;
            }

            string[] fields = line.Split(delimiter[0]);
            List<string> kept = [];
            for (int index = 1; index <= fields.Length; index++)
            {
                if (Selected(ranges, index) != complement)
                {
                    kept.Add(fields[index - 1]);
                }
            }

            await context.StandardOutput.WriteLineAsync(string.Join(outputDelimiter ?? delimiter, kept));
        }

        return failed.Value ? 1 : 0;
    }

    private static bool Selected(List<(int From, int To)> ranges, int index) =>
        ranges.Any(range => index >= range.From && index <= range.To);

    /// <summary>Parses <c>1,3-5,7-</c> into inclusive ranges; null when invalid.</summary>
    internal static List<(int From, int To)>? ParseList(string list)
    {
        List<(int, int)> ranges = [];
        foreach (string part in list.Split(','))
        {
            if (part.Length == 0)
            {
                return null;
            }

            int dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(part, out int single) || single < 1)
                {
                    return null;
                }

                ranges.Add((single, single));
                continue;
            }

            string left = part[..dash];
            string right = part[(dash + 1)..];
            int from = left.Length == 0 ? 1 : int.TryParse(left, out int l) && l >= 1 ? l : -1;
            int to = right.Length == 0 ? int.MaxValue : int.TryParse(right, out int r) && r >= 1 ? r : -1;
            if (from < 0 || to < 0 || from > to || (left.Length == 0 && right.Length == 0))
            {
                return null;
            }

            ranges.Add((from, to));
        }

        return ranges;
    }
}
