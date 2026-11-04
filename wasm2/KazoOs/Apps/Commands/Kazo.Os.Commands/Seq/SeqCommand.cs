using System.Globalization;
using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Seq;

/// <summary>
/// <c>seq [-s SEP] [-w] [FIRST [INCREMENT]] LAST</c> (ADR 0064): prints numbers from FIRST to
/// LAST, integers or decimals, <c>-w</c> padding them to equal width. Output streams, so
/// <c>seq 1 1000000 | head</c> stops early through the broken pipe.
/// </summary>
public sealed class SeqCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> messages = context.App.GetLocalizer<Strings>();
        string separator = "\n";
        bool equalWidth = false;
        List<string> numbers = [];
        IReadOnlyList<string> args = context.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg == "-s" && i + 1 < args.Count)
            {
                separator = args[++i];
            }
            else if (arg.StartsWith("-s", StringComparison.Ordinal) && arg.Length > 2)
            {
                separator = arg[2..];
            }
            else if (arg == "-w")
            {
                equalWidth = true;
            }
            else
            {
                numbers.Add(arg);
            }
        }

        if (numbers.Count is 0 or > 3)
        {
            await context.StandardError.WriteLineAsync(numbers.Count == 0 ? messages["MissingOperand"] : messages["ExtraOperand", numbers[3]]);
            return 1;
        }

        decimal[] values = new decimal[numbers.Count];
        for (int i = 0; i < numbers.Count; i++)
        {
            if (!decimal.TryParse(numbers[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                await context.StandardError.WriteLineAsync(messages["InvalidArgument", numbers[i]]);
                return 1;
            }
        }

        decimal first = values.Length > 1 ? values[0] : 1;
        decimal step = values.Length == 3 ? values[1] : 1;
        decimal last = values[^1];
        if (step == 0)
        {
            await context.StandardError.WriteLineAsync(messages["ZeroIncrement"]);
            return 1;
        }

        int decimals = numbers.Take(numbers.Count).Max(n => n.Contains('.') ? n.Length - n.IndexOf('.') - 1 : 0);
        string format = decimals > 0 ? "0." + new string('0', decimals) : "0";
        int width = equalWidth
            ? Math.Max(first.ToString(format, CultureInfo.InvariantCulture).Length, last.ToString(format, CultureInfo.InvariantCulture).Length)
            : 0;
        StringBuilder chunk = new();
        bool firstItem = true;
        long count = 0;
        for (decimal value = first; step > 0 ? value <= last : value >= last; value += step)
        {
            if (!firstItem)
            {
                chunk.Append(separator);
            }

            firstItem = false;
            string text = value.ToString(format, CultureInfo.InvariantCulture);
            chunk.Append(equalWidth ? text.PadLeft(width, '0') : text);
            if (++count % 512 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await context.StandardOutput.WriteAsync(chunk.ToString());
                chunk.Clear();
            }
        }

        if (!firstItem)
        {
            chunk.Append('\n');
        }

        await context.StandardOutput.WriteAsync(chunk.ToString());
        return 0;
    }
}
