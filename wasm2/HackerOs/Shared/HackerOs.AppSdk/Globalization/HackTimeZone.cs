using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace HackerOs.AppSdk.Globalization;

/// <summary>
/// The local time of HackerOS (ADR 0193): which time zone a process shows its dates in, and how to
/// convert the OS's UTC time (<see cref="IAppExecutionContext.Clock"/>) to it.
/// </summary>
/// <remarks>
/// <para>
/// A time zone is an IANA identifier (<c>America/Toronto</c>, <c>Europe/Paris</c>, <c>UTC</c>) or
/// <see cref="Automatic"/>, the device's own zone as the browser reports it. The session's zone is
/// the <c>timeZone</c> key of <c>datetime.config</c> (Settings &gt; Date and time); the <c>TZ</c>
/// variable of a process overrides it, as on Linux (<c>TZ=UTC ls -l</c>).
/// </para>
/// <para>
/// <see cref="Current"/>, <see cref="Now"/> and <see cref="ToLocal(DateTimeOffset)"/> read the app
/// running the current code (ambient context of ADR 0043); the other members are pure. Apps use
/// them instead of <see cref="DateTimeOffset.ToLocalTime"/> or <see cref="TimeZoneInfo.Local"/>,
/// which ignore the user's choice. No capability is needed.
/// </para>
/// </remarks>
public static class HackTimeZone
{
    /// <summary>The environment variable that overrides a process's time zone (POSIX <c>TZ</c>).</summary>
    public const string Variable = "TZ";

    /// <summary>The setting value meaning "the device's own zone": <c>auto</c>.</summary>
    public const string Automatic = "auto";

    private static readonly Lazy<IReadOnlyList<TimeZoneInfo>> AvailableZones = new(LoadAvailable);

    /// <summary>Gets the device's own time zone, as the browser (or the host machine) reports it.</summary>
    public static TimeZoneInfo Device => TimeZoneInfo.Local;

    /// <summary>
    /// Gets the IANA time zones a user can choose, sorted by identifier: every <c>Region/City</c>
    /// zone the runtime knows, plus <c>UTC</c>.
    /// </summary>
    public static IReadOnlyList<TimeZoneInfo> Available => AvailableZones.Value;

    /// <summary>Gets the time zone of the app running the current code.</summary>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static TimeZoneInfo Current => HackExecutionContext.Current.TimeZone;

    /// <summary>Gets the OS's current time in the time zone of the app running the current code.</summary>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static DateTimeOffset Now
    {
        get
        {
            IAppExecutionContext context = HackExecutionContext.Current;
            return TimeZoneInfo.ConvertTime(context.Clock.UtcNow, context.TimeZone);
        }
    }

    /// <summary>Converts <paramref name="time"/> to the time zone of the app running the current code.</summary>
    /// <param name="time">Any instant, usually a UTC timestamp of the OS.</param>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static DateTimeOffset ToLocal(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, Current);

    /// <summary>Converts <paramref name="time"/> to <paramref name="zone"/>.</summary>
    /// <param name="time">Any instant.</param>
    /// <param name="zone">The target zone.</param>
    public static DateTimeOffset ToLocal(DateTimeOffset time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(time, zone);
    }

    /// <summary>
    /// Finds the zone named <paramref name="id"/>: an IANA identifier, with or without the POSIX
    /// <c>:</c> prefix (<c>TZ=:Europe/Paris</c>). Fails for <see langword="null"/>, blank,
    /// <see cref="Automatic"/> and unknown names.
    /// </summary>
    /// <param name="id">The name, as written in <c>TZ</c> or in <c>datetime.config</c>.</param>
    /// <param name="zone">The zone when found.</param>
    public static bool TryFind(string? id, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        zone = null;
        string name = (id ?? string.Empty).Trim().TrimStart(':');
        if (name.Length == 0 || string.Equals(name, Automatic, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(name, "UTC", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Etc/UTC", StringComparison.OrdinalIgnoreCase))
        {
            zone = TimeZoneInfo.Utc;
            return true;
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(name, out TimeZoneInfo? found))
        {
            return false;
        }

        zone = found;
        return true;
    }

    /// <summary>Returns the zone named <paramref name="id"/>, or <paramref name="fallback"/> (the device's zone by default) when it names none.</summary>
    /// <param name="id">An IANA identifier, <see cref="Automatic"/>, or <see langword="null"/>.</param>
    /// <param name="fallback">The zone used when <paramref name="id"/> names none; <see cref="Device"/> when <see langword="null"/>.</param>
    public static TimeZoneInfo Resolve(string? id, TimeZoneInfo? fallback = null) =>
        TryFind(id, out TimeZoneInfo? zone) ? zone : fallback ?? Device;

    /// <summary>
    /// Returns the IANA identifier of <paramref name="zone"/> (<c>America/Toronto</c>), converting a
    /// Windows identifier when the runtime uses those; <c>UTC</c> for UTC.
    /// </summary>
    /// <param name="zone">The zone.</param>
    public static string GetId(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (zone.Equals(TimeZoneInfo.Utc) || zone.Id is "UTC" or "Etc/UTC" or "Coordinated Universal Time")
        {
            return "UTC";
        }

        if (zone.HasIanaId)
        {
            return zone.Id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out string? iana) ? iana : zone.Id;
    }

    /// <summary>Formats the UTC offset of <paramref name="zone"/> at <paramref name="time"/>: <c>UTC-04:00</c>, <c>UTC+05:30</c>, <c>UTC</c>.</summary>
    /// <param name="zone">The zone.</param>
    /// <param name="time">The instant (the offset changes with daylight saving time).</param>
    public static string FormatOffset(TimeZoneInfo zone, DateTimeOffset time)
    {
        ArgumentNullException.ThrowIfNull(zone);
        TimeSpan offset = zone.GetUtcOffset(time);
        if (offset == TimeSpan.Zero)
        {
            return "UTC";
        }

        char sign = offset < TimeSpan.Zero ? '-' : '+';
        TimeSpan magnitude = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{magnitude.Hours:00}:{magnitude.Minutes:00}");
    }

    private static IReadOnlyList<TimeZoneInfo> LoadAvailable()
    {
        SortedDictionary<string, TimeZoneInfo> zones = new(StringComparer.Ordinal) { ["UTC"] = TimeZoneInfo.Utc };
        IEnumerable<TimeZoneInfo> system;
        try
        {
            system = TimeZoneInfo.GetSystemTimeZones();
        }
        catch (Exception exception) when (exception is InvalidTimeZoneException or System.Security.SecurityException or IOException)
        {
            // A runtime without time zone data still offers UTC and the device's zone.
            system = [];
        }

        foreach (TimeZoneInfo zone in system.Append(Device))
        {
            string id = GetId(zone);
            // Region/City only: legacy aliases (EST5EDT, Etc/GMT+3…) are not listed, as in GNOME.
            if (id.Contains('/', StringComparison.Ordinal) && !id.StartsWith("Etc/", StringComparison.Ordinal)
                && TryFind(id, out TimeZoneInfo? found))
            {
                zones.TryAdd(id, found);
            }
        }

        return [.. zones.Values];
    }
}
