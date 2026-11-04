using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// The window manager's behaviour settings (ADR 0127 §5), a layered OS document (ADR 0044):
/// <c>/etc/hackeros/window-manager.config</c> holds the machine default chosen by an administrator,
/// and a user may override it in <c>~/.config/hackeros/window-manager.config</c> unless the key is
/// listed under <c>[locked]</c>.
/// </summary>
/// <remarks>
/// <code>
/// [unresponsive]
/// # Show "« App » ne répond pas : Attendre | Terminer l'application" over a window that stops
/// # answering. false keeps only the "(Ne répond pas)" suffix and the greyed content.
/// dialog = true
/// </code>
/// The threshold itself is not here: it is <c>Cpu:UnresponsiveThresholdMs</c> of the host's resource
/// accounting (ADR 0066), shared with the "Not responding" state of <c>/proc</c> and the task manager.
/// </remarks>
public static class WindowManagerSettingsDocuments
{
    /// <summary>Document id, the file name without <c>.config</c>.</summary>
    public const string DocumentId = "window-manager";

    /// <summary>Section of the unresponsive-window behaviour.</summary>
    public const string UnresponsiveGroup = "unresponsive";

    /// <summary>Key, inside <see cref="UnresponsiveGroup"/>, that turns the "not responding" dialog on or off.</summary>
    public const string DialogKey = "dialog";

    /// <summary>Gets the declared schema.</summary>
    public static SettingsSchema Schema { get; } = new(
        schemaVersion: 1,
        fields:
        [
            new SettingFieldDeclaration(DialogKey, SettingValueType.Boolean, "true", SettingSensitivity.Public, Group: UnresponsiveGroup)
        ]);

    /// <summary>Gets the layered document.</summary>
    public static OsSettingsDocument Document { get; } = new(DocumentId, Schema);

    /// <summary>
    /// Gets whether a merged snapshot shows the "not responding" dialog. Anything but a literal
    /// <c>false</c> keeps it on, so a typo never hides the only way to end a frozen app.
    /// </summary>
    /// <param name="snapshot">Snapshot of <see cref="Document"/>.</param>
    public static bool IsUnresponsiveDialogEnabled(LayeredConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return !(snapshot.Values.TryGetValue($"{UnresponsiveGroup}.{DialogKey}", out string? value)
            && string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase));
    }
}
