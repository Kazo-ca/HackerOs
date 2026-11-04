using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Commands.Sleep;

/// <summary>
/// <c>sleep NUMBER[smhd]...</c> (ADR 0064): waits for the sum of its arguments, which may be
/// fractional (<c>sleep 0.5</c>). Ctrl+C (or <c>kill</c>) ends it early with 130.
/// </summary>
public sealed class SleepCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();
        if (context.Arguments.Count == 0)
        {
            await context.StandardError.WriteLineAsync(text["MissingOperand"]);
            return 1;
        }

        double seconds = 0;
        foreach (string argument in context.Arguments)
        {
            if (!TryParseDuration(argument, out double value))
            {
                await context.StandardError.WriteLineAsync(text["InvalidInterval", argument]);
                return 1;
            }

            seconds += value;
        }

        await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, int.MaxValue / 1000.0)), cancellationToken);
        return 0;
    }

    /// <summary>Parses <c>1.5</c>, <c>10s</c>, <c>2m</c>, <c>1h</c>, <c>1d</c>.</summary>
    internal static bool TryParseDuration(string text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        double multiplier = text[^1] switch
        {
            's' => 1,
            'm' => 60,
            'h' => 3600,
            'd' => 86400,
            _ => 0
        };
        string number = multiplier == 0 ? text : text[..^1];
        multiplier = multiplier == 0 ? 1 : multiplier;
        if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) || value < 0)
        {
            return false;
        }

        seconds = value * multiplier;
        return true;
    }
}
