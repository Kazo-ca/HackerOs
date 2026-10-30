using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Theming.Abstractions.IconPacks;

namespace HackerOs.Platform.Core.Appearance;

/// <summary>
/// Defines the layered icon document (ADR 0203 §3): <c>/etc/hackeros/icons.config</c> holds the
/// machine's pack, and each user overrides it in <c>~/.config/hackeros/icons.config</c>. It is a
/// document of its own, apart from <c>appearance.config</c>, as the icon theme is apart from the GTK
/// theme on a Linux desktop.
/// </summary>
public static class IconPackSettingsDocuments
{
    /// <summary>Gets the declared schema: one free string, <see cref="IconPackLibrary.BuiltInId"/> by default.</summary>
    public static SettingsSchema Schema { get; } = new(
        schemaVersion: 1,
        fields:
        [
            // Pack ids are open strings, like theme ids (ADR 0103): an id that names no installed pack
            // keeps the built-in icons, as a desktop does with a deleted icon theme.
            new SettingFieldDeclaration(IconPackLibrary.PackIdKey, SettingValueType.String, IconPackLibrary.BuiltInId, SettingSensitivity.Public)
        ]);

    /// <summary>Gets the layered document.</summary>
    public static OsSettingsDocument Document { get; } = new(IconPackLibrary.SettingsDocumentId, Schema);

    /// <summary>Reads the selected pack id of a merged snapshot.</summary>
    /// <param name="snapshot">Snapshot of <see cref="Document"/>.</param>
    public static string ToPackId(LayeredConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Values.TryGetValue(IconPackLibrary.PackIdKey, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : IconPackLibrary.BuiltInId;
    }
}
