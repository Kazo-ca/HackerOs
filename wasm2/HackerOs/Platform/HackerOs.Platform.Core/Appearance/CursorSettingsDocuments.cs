using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Theming.Abstractions;

namespace HackerOs.Platform.Core.Appearance;

/// <summary>
/// Defines the layered cursor document (ADR 0205 §3): <c>/etc/hackeros/cursors.config</c> holds the
/// machine's cursor theme (also used by the sign-in screen), and each user overrides it in
/// <c>~/.config/hackeros/cursors.config</c>, like <c>XCURSOR_THEME</c> or the <c>[Icon Theme]</c>
/// of <c>~/.icons/default/index.theme</c> on Linux.
/// </summary>
public static class CursorSettingsDocuments
{
    /// <summary>Key of the cursor theme id.</summary>
    public const string ThemeKey = "theme";

    /// <summary>
    /// Gets the declared schema. The id is an open string: a theme of <c>/etc/hackeros/cursors/</c>
    /// or <c>~/.config/hackeros/cursors/</c> may be chosen; an id that names no theme draws the
    /// host's cursors (<see cref="CursorThemeLibraryResult.Resolve"/>).
    /// </summary>
    public static SettingsSchema Schema { get; } = new(
        schemaVersion: 1,
        fields:
        [
            new SettingFieldDeclaration(ThemeKey, SettingValueType.String, CursorThemeCatalog.SystemId, SettingSensitivity.Public)
        ]);

    /// <summary>Gets the layered document.</summary>
    public static OsSettingsDocument Document { get; } = new("cursors", Schema);

    /// <summary>Gets the theme id of a merged snapshot.</summary>
    /// <param name="snapshot">Snapshot of <see cref="Document"/>.</param>
    public static string ToThemeId(LayeredConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Values[ThemeKey];
    }
}
