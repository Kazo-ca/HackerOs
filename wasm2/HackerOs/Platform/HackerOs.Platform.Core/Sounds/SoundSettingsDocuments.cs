using System.Globalization;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Simulation.Abstractions.Sounds;

namespace HackerOs.Platform.Core.Sounds;

/// <summary>
/// Defines the layered sound document (ADR 0204 section 3): <c>/etc/hackeros/sound.config</c> holds
/// the machine's defaults, and each user overrides them in <c>~/.config/hackeros/sound.config</c>.
/// </summary>
public static class SoundSettingsDocuments
{
    /// <summary>File name of the document, without <c>.config</c>.</summary>
    public const string DocumentId = "sound";

    /// <summary>Key that turns the event sounds on or off.</summary>
    public const string EnabledKey = "enabled";

    /// <summary>Key of the sound theme: a folder name of <c>/usr/share/sounds</c> or <c>~/.local/share/sounds</c>.</summary>
    public const string ThemeKey = "theme";

    /// <summary>Key of the volume, 0 to 100.</summary>
    public const string VolumeKey = "volume";

    /// <summary>Volume of a fresh installation.</summary>
    public const int DefaultVolume = 60;

    /// <summary>Gets the declared schema.</summary>
    public static SettingsSchema Schema { get; } = new(
        schemaVersion: 1,
        fields:
        [
            new SettingFieldDeclaration(EnabledKey, SettingValueType.Boolean, "true", SettingSensitivity.Public),
            new SettingFieldDeclaration(ThemeKey, SettingValueType.String, OsSoundEvents.DefaultThemeId, SettingSensitivity.Public),
            new SettingFieldDeclaration(
                VolumeKey, SettingValueType.Integer, DefaultVolume.ToString(CultureInfo.InvariantCulture), SettingSensitivity.Public)
        ]);

    /// <summary>Gets the layered document.</summary>
    public static OsSettingsDocument Document { get; } = new(DocumentId, Schema);

    /// <summary>Reads the merged values of a snapshot, falling back to the defaults on a missing or invalid value.</summary>
    /// <param name="snapshot">Snapshot of <see cref="Document"/>.</param>
    public static SoundPreferences ToPreferences(LayeredConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool enabled = !snapshot.Values.TryGetValue(EnabledKey, out string? enabledText)
            || !bool.TryParse(enabledText, out bool parsed)
            || parsed;
        string theme = snapshot.Values.TryGetValue(ThemeKey, out string? themeText) && OsSoundEvents.IsValidThemeId(themeText?.Trim())
            ? themeText!.Trim()
            : OsSoundEvents.DefaultThemeId;
        int volume = snapshot.Values.TryGetValue(VolumeKey, out string? volumeText)
            && long.TryParse(volumeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
                ? (int)Math.Clamp(value, 0, 100)
                : DefaultVolume;
        return new SoundPreferences(enabled, theme, volume);
    }
}

/// <summary>The effective sound preferences of the session.</summary>
/// <param name="Enabled">Whether the OS plays event sounds.</param>
/// <param name="Theme">Identifier of the sound theme.</param>
/// <param name="Volume">Volume from 0 to 100.</param>
public sealed record SoundPreferences(bool Enabled, string Theme, int Volume)
{
    /// <summary>Gets the preferences of a fresh installation.</summary>
    public static SoundPreferences Default { get; } =
        new(true, OsSoundEvents.DefaultThemeId, SoundSettingsDocuments.DefaultVolume);

    /// <summary>Gets whether a sound would be heard.</summary>
    public bool IsAudible => Enabled && Volume > 0;
}
