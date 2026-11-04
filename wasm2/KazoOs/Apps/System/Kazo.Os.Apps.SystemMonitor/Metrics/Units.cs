using System.Globalization;

namespace Kazo.Os.Apps.SystemMonitor.Metrics;

/// <summary>
/// Human-readable sizes, rates and durations, in binary units like <c>free -h</c>.
/// </summary>
/// <remarks>
/// The one-argument overloads format with the invariant culture (stable output, as <c>/proc</c>
/// tools print). The overloads taking a culture follow the process's locale (ADR 0104): its decimal
/// and group separators, and, in French, the unit symbols (<c>Kio</c>, <c>Mio</c>), the space before
/// <c>%</c> and <c>j</c> for days.
/// </remarks>
public static class Units
{
    private static readonly string[] ByteUnits = ["B", "KiB", "MiB", "GiB", "TiB"];
    private static readonly string[] FrenchByteUnits = ["o", "Kio", "Mio", "Gio", "Tio"];

    /// <summary>Narrow no-break space, which French typography puts before <c>%</c>.</summary>
    private const string NarrowNoBreakSpace = " ";

    /// <summary>Formats a byte count (<c>1.5 MiB</c>).</summary>
    /// <param name="bytes">Byte count.</param>
    public static string Bytes(double bytes) => Bytes(bytes, CultureInfo.InvariantCulture);

    /// <summary>Formats a byte count in <paramref name="culture"/> (<c>1,5 Mio</c> in French).</summary>
    /// <param name="bytes">Byte count.</param>
    /// <param name="culture">The culture of the numbers and unit symbols.</param>
    public static string Bytes(double bytes, CultureInfo culture)
    {
        if (!double.IsFinite(bytes) || bytes < 0)
        {
            bytes = 0;
        }

        int unit = 0;
        while (bytes >= 1024 && unit < ByteUnits.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        string format = unit == 0 || bytes >= 100 ? "0" : "0.#";
        string[] symbols = IsFrench(culture) ? FrenchByteUnits : ByteUnits;
        return $"{bytes.ToString(format, culture)} {symbols[unit]}";
    }

    /// <summary>Formats a throughput (<c>12 KiB/s</c>).</summary>
    /// <param name="bytesPerSecond">Bytes per second.</param>
    public static string Rate(double bytesPerSecond) => $"{Bytes(bytesPerSecond)}/s";

    /// <summary>Formats a throughput in <paramref name="culture"/> (<c>12 Kio/s</c> in French).</summary>
    public static string Rate(double bytesPerSecond, CultureInfo culture) => $"{Bytes(bytesPerSecond, culture)}/s";

    /// <summary>Formats a percentage with one decimal (<c>12.5%</c>).</summary>
    /// <param name="percent">Value in percent.</param>
    public static string Percent(double percent) => Percent(percent, CultureInfo.InvariantCulture);

    /// <summary>Formats a percentage with one decimal in <paramref name="culture"/> (<c>12,5 %</c> in French).</summary>
    public static string Percent(double percent, CultureInfo culture) =>
        $"{(double.IsFinite(percent) ? percent : 0).ToString("0.0", culture)}{(IsFrench(culture) ? NarrowNoBreakSpace : string.Empty)}%";

    /// <summary>Formats a count with thousands separators (<c>12,345</c>).</summary>
    /// <param name="value">Count.</param>
    public static string Count(long value) => Count(value, CultureInfo.InvariantCulture);

    /// <summary>Formats a count with the group separator of <paramref name="culture"/>.</summary>
    public static string Count(long value, CultureInfo culture) => value.ToString("#,0", culture);

    /// <summary>Formats an elapsed time like <c>uptime</c> (<c>2d 03:14:15</c>).</summary>
    /// <param name="duration">Elapsed time.</param>
    public static string Duration(TimeSpan duration) => Duration(duration, CultureInfo.InvariantCulture);

    /// <summary>Formats an elapsed time in <paramref name="culture"/> (<c>2 j 03:14:15</c> in French).</summary>
    public static string Duration(TimeSpan duration, CultureInfo culture)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        string clock = duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        if (duration.Days == 0)
        {
            return clock;
        }

        string days = duration.Days.ToString(CultureInfo.InvariantCulture);
        return IsFrench(culture) ? $"{days} j {clock}" : $"{days}d {clock}";
    }

    private static bool IsFrench(CultureInfo culture) =>
        string.Equals(culture.TwoLetterISOLanguageName, "fr", StringComparison.OrdinalIgnoreCase);
}
