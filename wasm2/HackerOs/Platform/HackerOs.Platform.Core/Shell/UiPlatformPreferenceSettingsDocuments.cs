using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Settings;

namespace HackerOs.Platform.Core.Shell;

/// <summary>
/// Defines the layered UI-platform-preference document (ADR 0044 section 8):
/// <c>/etc/hackeros/ui-platform-preference.config</c> then <c>~/.config/hackeros/ui-platform-preference.config</c>,
/// holding whether the shell follows automatic platform detection or an explicit Desktop/Mobile
/// choice (see doc/wasm/dev/old/mobile-interface-platform-plan.md §6).
/// </summary>
public static class UiPlatformPreferenceSettingsDocuments
{
    /// <summary>Key of the selection source, <c>auto</c> or <c>explicit</c>.</summary>
    public const string SelectionSourceKey = "selectionSource";

    /// <summary>Key of the explicit platform id; empty when automatic.</summary>
    public const string ExplicitPlatformKey = "explicitPlatformId";

    /// <summary>Gets the declared schema; the clean default is automatic detection.</summary>
    public static SettingsSchema Schema { get; } = new(
        schemaVersion: 2,
        fields:
        [
            new SettingFieldDeclaration(
                SelectionSourceKey,
                SettingValueType.Enum,
                "auto",
                SettingSensitivity.Public,
                ["auto", "explicit"]),
            new SettingFieldDeclaration(
                ExplicitPlatformKey,
                SettingValueType.String,
                string.Empty,
                SettingSensitivity.Public)
        ]);

    /// <summary>Gets the layered document.</summary>
    public static OsSettingsDocument Document { get; } = new("ui-platform-preference", Schema);
}
