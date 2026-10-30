using System.Globalization;
using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace HackerOs.Commands.Date;

/// <summary>
/// <c>date</c>: prints the OS's current time in the process's time zone (ADR 0193), like GNU
/// <c>date</c> in the C locale: <c>Thu Oct 29 09:57:01 -04 2026</c>.
/// </summary>
/// <remarks>
/// Supports <c>-u</c> (<c>--utc</c>, <c>--universal</c>) for UTC, <c>-I</c> (<c>--iso-8601</c>) for
/// the ISO 8601 date, <c>-R</c> (<c>--rfc-email</c>) for RFC 5322, and <c>+FORMAT</c> with the usual
/// <c>strftime</c> conversions. The zone is the process's <c>TZ</c>, else the session's (Settings &gt;
/// Date and time), so <c>TZ=Asia/Tokyo date</c> works as on Linux. Setting the clock is not supported:
/// the OS's time is the simulation clock.
/// </remarks>
public sealed class DateCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <summary>The default output, as GNU <c>date</c> prints it in the C locale.</summary>
    internal const string DefaultFormat = "%a %b %e %H:%M:%S %Z %Y";

    private const string RfcEmailFormat = "%a, %d %b %Y %H:%M:%S %z";
    private const string IsoDateFormat = "%F";

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IStringLocalizer text = context.App.GetLocalizer<Strings>();
        bool utc = false;
        string? format = null;
        bool customFormat = false;
        foreach (string argument in context.Arguments)
        {
            switch (argument)
            {
                case "--help":
                    await context.StandardOutput.WriteLineAsync(text["Usage"]);
                    return 0;
                case "-u" or "--utc" or "--universal":
                    utc = true;
                    break;
                case "-I" or "--iso-8601":
                    format = IsoDateFormat;
                    break;
                case "-R" or "--rfc-email":
                    format = RfcEmailFormat;
                    break;
                case { Length: > 0 } when argument[0] == '+':
                    if (customFormat)
                    {
                        await context.StandardError.WriteLineAsync(text["ExtraOperand", argument]);
                        return 1;
                    }

                    customFormat = true;
                    format = argument[1..];
                    break;
                case { Length: > 1 } when argument[0] == '-':
                    await context.StandardError.WriteLineAsync(text["InvalidOption", argument]);
                    return 1;
                default:
                    await context.StandardError.WriteLineAsync(text["CannotSet", argument]);
                    return 1;
            }
        }

        TimeZoneInfo zone = utc ? TimeZoneInfo.Utc : context.App.TimeZone;
        DateTimeOffset now = TimeZoneInfo.ConvertTime(context.App.Clock.UtcNow, zone);
        await context.StandardOutput.WriteLineAsync(Format(now, zone, format ?? DefaultFormat));
        return 0;
    }

    /// <summary>
    /// Formats <paramref name="time"/> with <c>strftime</c> conversions, in the C locale:
    /// <c>%Y %y %m %d %e %H %I %M %S %p %Z %z %a %A %b %h %B %j %s %F %T %D %R %n %t %%</c>.
    /// An unknown conversion is printed as written, like GNU <c>date</c>.
    /// </summary>
    /// <param name="time">The time, already in <paramref name="zone"/>.</param>
    /// <param name="zone">Its zone, for <c>%Z</c>.</param>
    /// <param name="format">The format, without the leading <c>+</c>.</param>
    internal static string Format(DateTimeOffset time, TimeZoneInfo zone, string format)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        StringBuilder output = new();
        for (int index = 0; index < format.Length; index++)
        {
            char current = format[index];
            if (current != '%' || index == format.Length - 1)
            {
                output.Append(current);
                continue;
            }

            char conversion = format[++index];
            string? part = conversion switch
            {
                'Y' => time.ToString("yyyy", c),
                'y' => time.ToString("yy", c),
                'm' => time.ToString("MM", c),
                'd' => time.ToString("dd", c),
                'e' => time.Day.ToString(c).PadLeft(2),
                'H' => time.ToString("HH", c),
                'I' => time.ToString("hh", c),
                'M' => time.ToString("mm", c),
                'S' => time.ToString("ss", c),
                'p' => time.Hour < 12 ? "AM" : "PM",
                'Z' => ZoneName(zone, time.Offset),
                'z' => NumericOffset(time.Offset),
                'a' => time.ToString("ddd", c),
                'A' => time.ToString("dddd", c),
                'b' or 'h' => time.ToString("MMM", c),
                'B' => time.ToString("MMMM", c),
                'j' => time.DayOfYear.ToString("000", c),
                's' => time.ToUnixTimeSeconds().ToString(c),
                'F' => time.ToString("yyyy-MM-dd", c),
                'T' => time.ToString("HH:mm:ss", c),
                'D' => time.ToString("MM/dd/yy", c),
                'R' => time.ToString("HH:mm", c),
                'n' => "\n",
                't' => "\t",
                '%' => "%",
                _ => null
            };
            output.Append(part ?? "%" + conversion);
        }

        return output.ToString();
    }

    /// <summary>
    /// The zone name of <c>%Z</c>: <c>UTC</c>, or the offset as the IANA database writes zones
    /// without an abbreviation (<c>-04</c>, <c>+0530</c>), since .NET has no zone abbreviations.
    /// </summary>
    private static string ZoneName(TimeZoneInfo zone, TimeSpan offset)
    {
        if (zone.Equals(TimeZoneInfo.Utc))
        {
            return "UTC";
        }

        string numeric = NumericOffset(offset);
        return offset.Minutes == 0 ? numeric[..3] : numeric;
    }

    /// <summary>The offset of <c>%z</c>: <c>-0400</c>, <c>+0530</c>.</summary>
    private static string NumericOffset(TimeSpan offset)
    {
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        TimeSpan magnitude = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{magnitude.Hours:00}{magnitude.Minutes:00}");
    }
}
