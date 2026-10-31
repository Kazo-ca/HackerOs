using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Settings;

namespace HackerOs.Platform.Core.Notifications;

/// <summary>
/// Defines the layered notification document (ADR 0216 §3): <c>/etc/hackeros/notifications.config</c>
/// holds the machine's default, and each user overrides it in <c>~/.config/hackeros/notifications.config</c>.
/// </summary>
public static class NotificationSettingsDocuments
{
    /// <summary>File name of the document, without <c>.config</c>.</summary>
    public const string DocumentId = "notifications";

    /// <summary>Key of the "Do not disturb" mode: no bubble and no sound for new notifications.</summary>
    public const string DoNotDisturbKey = "doNotDisturb";

    /// <summary>Gets the declared schema.</summary>
    public static SettingsSchema Schema { get; } = new(
        schemaVersion: 1,
        fields:
        [
            new SettingFieldDeclaration(DoNotDisturbKey, SettingValueType.Boolean, "false", SettingSensitivity.Public)
        ]);

    /// <summary>Gets the layered document.</summary>
    public static OsSettingsDocument Document { get; } = new(DocumentId, Schema);

    /// <summary>Reads whether "Do not disturb" is on; a missing or invalid value is off.</summary>
    /// <param name="snapshot">Snapshot of <see cref="Document"/>.</param>
    public static bool IsDoNotDisturb(LayeredConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Values.TryGetValue(DoNotDisturbKey, out string? text)
            && bool.TryParse(text?.Trim(), out bool value)
            && value;
    }
}
