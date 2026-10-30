using HackerOs.AppSdk.Globalization;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Settings;

namespace HackerOs.Platform.Core.Globalization;

/// <summary>
/// Defines the layered date and time document (ADR 0193 section 2):
/// <c>/etc/hackeros/datetime.config</c> holds the machine's time zone (also used by the sign-in
/// screen), and each user overrides it in <c>~/.config/hackeros/datetime.config</c>.
/// </summary>
public static class TimeZoneSettingsDocuments
{
    /// <summary>Key of the time zone: <c>auto</c> (the device's zone) or an IANA identifier.</summary>
    public const string TimeZoneKey = "timeZone";

    /// <summary>Gets the declared schema: one free string, <c>auto</c> by default.</summary>
    public static SettingsSchema Schema { get; } = new(
        schemaVersion: 1,
        fields:
        [
            new SettingFieldDeclaration(TimeZoneKey, SettingValueType.String, HackTimeZone.Automatic, SettingSensitivity.Public)
        ]);

    /// <summary>Gets the layered document.</summary>
    public static OsSettingsDocument Document { get; } = new("datetime", Schema);

    /// <summary>Reads the <c>timeZone</c> value of a merged snapshot.</summary>
    /// <param name="snapshot">Snapshot of <see cref="Document"/>.</param>
    public static string ToSetting(LayeredConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Values.TryGetValue(TimeZoneKey, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : HackTimeZone.Automatic;
    }
}
