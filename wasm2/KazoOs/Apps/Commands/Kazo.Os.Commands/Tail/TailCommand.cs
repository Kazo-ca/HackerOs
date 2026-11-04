using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Common;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Tail;

/// <summary>
/// Implements the <c>tail</c> terminal command (`P4-W5-CMD-003`, options extended by ADR 0064):
/// the last 10 lines of each file or of standard input; <c>-n N</c> (or <c>-N</c>) lines,
/// <c>-n +N</c> from line N on, <c>-c N</c> characters, <c>-q</c>/<c>-v</c> headers, and
/// <c>-f</c> to follow a file: it keeps printing what is appended (a log under
/// <c>/var/log</c>) until Ctrl+C, checking the file every half second as inotify-less tail does.
/// </summary>
public sealed class TailCommand : TerminalAppBase
{
    /// <summary>How often <c>tail -f</c> looks at the file again.</summary>
    internal static TimeSpan FollowInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Initializes the command with its manifest.</summary>
    public TailCommand(AppManifest manifest) : base(manifest) { }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(
        TerminalExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        IStringLocalizer<Strings> messages = context.App.GetLocalizer<Strings>();

        long count = 10;
        bool bytes = false, fromStart = false, follow = false, quiet = false, verbose = false;
        List<string> files = [];
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            string? value = null;
            if (a is "-n" or "-c" or "--lines" or "--bytes" && i + 1 < args.Count)
            {
                value = args[++i];
                bytes = a is "-c" or "--bytes";
            }
            else if ((a.StartsWith("-n", StringComparison.Ordinal) || a.StartsWith("-c", StringComparison.Ordinal)) && a.Length > 2)
            {
                value = a[2..];
                bytes = a[1] == 'c';
            }
            else if (a.Length > 1 && a[0] is '-' or '+' && a[1..].All(char.IsAsciiDigit))
            {
                value = a[0] == '+' ? a : a[1..];
            }
            else if (a is "-f" or "-F" or "--follow")
            {
                follow = true;
                continue;
            }
            else if (a is "-q" or "--quiet" or "--silent")
            {
                quiet = true;
                continue;
            }
            else if (a is "-v" or "--verbose")
            {
                verbose = true;
                continue;
            }
            else
            {
                files.Add(a);
                continue;
            }

            fromStart = value.StartsWith('+');
            if (!long.TryParse(value.TrimStart('-', '+'), out count))
            {
                context.StandardError.WriteLine(messages[bytes ? "InvalidBytes" : "InvalidLines", value]);
                return 1;
            }
        }

        if (files.Count == 0)
        {
            files.Add("-");
        }

        int status = 0;
        bool headers = verbose || (files.Count > 1 && !quiet);
        Dictionary<string, int> seen = new(StringComparer.Ordinal);
        for (int index = 0; index < files.Count; index++)
        {
            string file = files[index];
            string? text = await CommandIO.ReadTextAsync(context, file, "tail", cancellationToken);
            if (text is null)
            {
                status = 1;
                continue;
            }

            seen[file] = text.Length;
            if (headers)
            {
                await context.StandardOutput.WriteLineAsync($"{(index > 0 ? "\n" : string.Empty)}==> {(file == "-" ? messages["StandardInput"] : file)} <==");
            }

            if (bytes)
            {
                int start = (int)Math.Clamp(fromStart ? count - 1 : text.Length - count, 0, text.Length);
                await context.StandardOutput.WriteAsync(text[start..]);
                continue;
            }

            List<string> lines = CommandIO.SplitLines(text);
            int skip = (int)Math.Clamp(fromStart ? count - 1 : lines.Count - count, 0, lines.Count);
            foreach (string line in lines.Skip(skip))
            {
                await context.StandardOutput.WriteLineAsync(line);
            }
        }

        if (!follow || files.All(file => file == "-"))
        {
            return status;
        }

        // -f: print what is appended until interrupted (Ctrl+C ends with 130, as SIGINT does).
        string? lastShown = files.Count > 1 ? files[^1] : null;
        while (true)
        {
            await Task.Delay(FollowInterval, cancellationToken);
            foreach (string file in files.Where(f => f != "-"))
            {
                string? text = await CommandIO.ReadTextAsync(context, file, "tail", cancellationToken);
                if (text is null)
                {
                    continue;
                }

                int previous = seen.GetValueOrDefault(file);
                if (text.Length < previous)
                {
                    await context.StandardError.WriteLineAsync(messages["Truncated", file]);
                    previous = 0;
                }

                if (text.Length > previous)
                {
                    if (files.Count > 1 && lastShown != file)
                    {
                        await context.StandardOutput.WriteLineAsync($"\n==> {file} <==");
                        lastShown = file;
                    }

                    await context.StandardOutput.WriteAsync(text[previous..]);
                }

                seen[file] = text.Length;
            }
        }
    }
}
