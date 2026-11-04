using System.Globalization;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.ServerConnection;

/// <summary>
/// The periodic synchronization settings (ADR 0101 section 4), a layered OS document (ADR 0044):
/// <c>/etc/hackeros/sync.config</c> holds the interval an administrator chose, and each user may
/// pause their own synchronization in <c>~/.config/hackeros/sync.config</c>.
/// </summary>
/// <remarks>
/// <code>
/// # /etc/hackeros/sync.config
/// intervalMinutes = 5
///
/// # ~/.config/hackeros/sync.config
/// paused = true
/// </code>
/// The interval is read from the system file only: a value in a user file is ignored, like a user
/// cannot change a cron job of root. <c>paused</c> follows the normal layering, so an administrator
/// can still force it with a <c>[locked]</c> section.
/// </remarks>
public static class SyncSettingsDocuments
{
    /// <summary>Key of the interval between two periodic passes, in minutes.</summary>
    public const string IntervalKey = "intervalMinutes";

    /// <summary>Key suspending the user's synchronization.</summary>
    public const string PausedKey = "paused";

    /// <summary>Default interval (ADR 0101 section 4).</summary>
    public const int DefaultIntervalMinutes = 5;

    /// <summary>Gets the declared schema.</summary>
    public static SettingsSchema Schema { get; } = new(
        schemaVersion: 1,
        fields:
        [
            new SettingFieldDeclaration(
                IntervalKey, SettingValueType.Integer, DefaultIntervalMinutes.ToString(CultureInfo.InvariantCulture), SettingSensitivity.Public),
            new SettingFieldDeclaration(PausedKey, SettingValueType.Boolean, "false", SettingSensitivity.Public)
        ]);

    /// <summary>Gets the layered document.</summary>
    public static OsSettingsDocument Document { get; } = new("sync", Schema);

    /// <summary>Gets the system-layer file, read alone for the interval.</summary>
    public static OsSettingsFile SystemFile { get; } = new(OsSettingsLocation.System, "sync.config");

    /// <summary>Reads the interval of the system file, bounded to one minute .. one day.</summary>
    /// <param name="systemContent">Content of <c>/etc/hackeros/sync.config</c>, or <see langword="null"/> when absent.</param>
    public static TimeSpan ReadInterval(string? systemContent)
    {
        LayeredConfigSnapshot snapshot = LayeredConfigResolver.Resolve(
            Schema, systemContent, userContent: null, Document.SystemPath.Value, userPath: string.Empty);
        int minutes = int.TryParse(snapshot.Values[IntervalKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : DefaultIntervalMinutes;
        return TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 24 * 60));
    }

    /// <summary>Gets whether a merged snapshot pauses the synchronization.</summary>
    /// <param name="snapshot">Snapshot of <see cref="Document"/>.</param>
    public static bool IsPaused(LayeredConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return string.Equals(snapshot.Values[PausedKey], "true", StringComparison.OrdinalIgnoreCase);
    }
}
