using System.Globalization;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>
/// The OOM killer's settings (ADR 0126, section 1), read from the <c>[oom-killer]</c> section of
/// <c>/etc/hackeros/memory.config</c>.
/// </summary>
/// <param name="Enabled"><c>enabled</c>: off by default; on, it still acts only where memory is measured.</param>
/// <param name="MinFreePercent">
/// <c>min-free-percent</c>: the OOM killer acts when <c>MemAvailable</c> stays under this share of
/// <c>MemTotal</c> for two samples in a row. <c>0</c> to <c>50</c>; <c>5</c> by default.
/// </param>
public sealed record OomKillerSettings(bool Enabled, int MinFreePercent)
{
    /// <summary>The settings of a fresh installation: off, 5 %.</summary>
    public static OomKillerSettings Default { get; } = new(false, MemorySettingsDocuments.DefaultMinFreePercent);
}

/// <summary>
/// Defines the memory document <c>/etc/hackeros/memory.config</c> (ADR 0126, section 1), system
/// layer only like <c>security.config</c>: it belongs to <c>system:system</c> with mode <c>0644</c>,
/// so only an administrator changes it (<c>sudo nano</c>, the approval prompt of ADR 0042, or
/// Settings &gt; Memory). An invalid value is ignored with a warning in <c>/var/log/syslog</c>.
/// </summary>
public static class MemorySettingsDocuments
{
    /// <summary>Document id, giving <c>memory.config</c>.</summary>
    public const string DocumentId = "memory";

    /// <summary>Section of the OOM killer's keys.</summary>
    public const string OomKillerGroup = "oom-killer";

    /// <summary>Key of the on/off switch.</summary>
    public const string EnabledKey = "enabled";

    /// <summary>Key of the threshold.</summary>
    public const string MinFreePercentKey = "min-free-percent";

    /// <summary>Default threshold: 5 % of <c>MemTotal</c>.</summary>
    public const int DefaultMinFreePercent = 5;

    /// <summary>Highest threshold accepted; above it, the value is lowered to it with a warning.</summary>
    public const int MaximumMinFreePercent = 50;

    /// <summary>Gets the qualified key of <see cref="EnabledKey"/>: <c>oom-killer.enabled</c>.</summary>
    public static string EnabledQualifiedKey => $"{OomKillerGroup}.{EnabledKey}";

    /// <summary>Gets the qualified key of <see cref="MinFreePercentKey"/>: <c>oom-killer.min-free-percent</c>.</summary>
    public static string MinFreePercentQualifiedKey => $"{OomKillerGroup}.{MinFreePercentKey}";

    /// <summary>Gets the declared schema.</summary>
    public static SettingsSchema Schema { get; } = new(
        schemaVersion: 1,
        fields:
        [
            new SettingFieldDeclaration(EnabledKey, SettingValueType.Boolean, "false", SettingSensitivity.Public, Group: OomKillerGroup),
            new SettingFieldDeclaration(
                MinFreePercentKey,
                SettingValueType.Integer,
                DefaultMinFreePercent.ToString(CultureInfo.InvariantCulture),
                SettingSensitivity.Public,
                Group: OomKillerGroup)
        ]);

    /// <summary>Gets the system-only layered document.</summary>
    public static OsSettingsDocument Document { get; } = new(DocumentId, Schema, SystemOnly: true);

    /// <summary>
    /// Gets the commented file a fresh installation ships (written once by
    /// <see cref="SettingsStoreToFilesMigration.MigrateSystemAsync"/>).
    /// </summary>
    public static string DefaultContent { get; } =
        "# /etc/hackeros/memory.config\n"
        + "# OOM killer (ADR 0126). When MemAvailable stays under min-free-percent of MemTotal\n"
        + "# for two seconds, the process with the highest /proc/<pid>/oom_score is killed and\n"
        + "# the kill is written to /var/log/kern.log (see dmesg). It also acts when a runtime\n"
        + "# nears the browser's real memory limit. It only acts where memory is measured\n"
        + "# (MemSource: measured): it never kills on simulated figures.\n"
        + "schemaVersion=1\n"
        + $"[{OomKillerGroup}]\n"
        + $"{EnabledKey}=false\n"
        + $"{MinFreePercentKey}={DefaultMinFreePercent.ToString(CultureInfo.InvariantCulture)}\n";

    /// <summary>Builds the settings from a merged snapshot; a threshold out of range is brought back into it.</summary>
    /// <param name="snapshot">Snapshot of <see cref="Document"/>.</param>
    /// <param name="warning">A line for <c>syslog</c> when the threshold was out of range, otherwise <see langword="null"/>.</param>
    public static OomKillerSettings ToSettings(LayeredConfigSnapshot snapshot, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        warning = null;
        bool enabled = snapshot.Values.TryGetValue(EnabledQualifiedKey, out string? on) && on == "true";
        int percent = DefaultMinFreePercent;
        if (snapshot.Values.TryGetValue(MinFreePercentQualifiedKey, out string? text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            percent = (int)Math.Clamp(value, 0, MaximumMinFreePercent);
            if (percent != value)
            {
                warning = $"memory.config: {MinFreePercentQualifiedKey}={value} is out of range 0-{MaximumMinFreePercent}; using {percent}";
            }
        }

        return new OomKillerSettings(enabled, percent);
    }

    /// <summary>Reads the effective settings; a missing or invalid file gives <see cref="OomKillerSettings.Default"/>.</summary>
    /// <param name="settings">OS settings service (system-layer reads are audited system operations).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async ValueTask<(OomKillerSettings Settings, string? Warning)> ReadAsync(
        IOsSettingsService settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        LayeredConfigSnapshot snapshot = await settings.ReadAsync(Document, cancellationToken).ConfigureAwait(false);
        OomKillerSettings value = ToSettings(snapshot, out string? warning);
        return (value, warning);
    }
}
