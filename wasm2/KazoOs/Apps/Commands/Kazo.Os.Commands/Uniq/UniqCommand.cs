using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Commands.Common;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Uniq;

/// <summary>
/// <c>uniq [-c] [-d] [-u] [-i] [INPUT [OUTPUT]]</c> (ADR 0064): collapses adjacent repeated
/// lines (so usually after <c>sort</c>); <c>-c</c> prefixes counts, <c>-d</c> keeps only the
/// repeated lines, <c>-u</c> only the unique ones, <c>-i</c> ignores case.
/// </summary>
public sealed class UniqCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        (List<string> options, List<string> operands) = CommandIO.SplitArguments(context.Arguments);
        bool count = false, repeated = false, unique = false, ignoreCase = false;
        foreach (char flag in options.SelectMany(option => option.TrimStart('-')))
        {
            switch (flag)
            {
                case 'c': count = true; break;
                case 'd': repeated = true; break;
                case 'u': unique = true; break;
                case 'i': ignoreCase = true; break;
                default:
                    await context.StandardError.WriteLineAsync(text["InvalidOption", flag]);
                    return 1;
            }
        }

        StringBuilder output = new();
        StrongBox<bool> failed = new(false);
        string? previous = null;
        int run = 0;
        StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        await foreach (string line in CommandIO.ReadLinesAsync(context, operands.Take(1).ToList(), "uniq", failed, cancellationToken))
        {
            if (previous is not null && string.Equals(previous, line, comparison))
            {
                run++;
                continue;
            }

            Flush();
            previous = line;
            run = 1;
        }

        Flush();
        if (operands.Count > 1)
        {
            try
            {
                await KazoFile.WriteAllTextAsync(operands[1], output.ToString(), cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                await context.StandardError.WriteLineAsync(CommandText.FileError(context, "uniq", operands[1], CommandText.Describe(context, exception)));
                return 1;
            }
        }
        else
        {
            await context.StandardOutput.WriteAsync(output.ToString());
        }

        return failed.Value ? 1 : 0;

        void Flush()
        {
            if (previous is null || (repeated && run < 2) || (unique && run > 1))
            {
                return;
            }

            output.Append(count ? $"{run,7} {previous}\n" : previous + "\n");
        }
    }
}
