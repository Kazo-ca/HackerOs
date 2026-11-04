using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>Layered mobile-shell preferences, editable by Settings through the public settings SDK.</summary>
public static class MobileSettingsDocuments
{
    /// <summary>Whether the Home screen shows extra mouse navigation controls and gesture hints.</summary>
    public const string MouseHelpersKey = "mouseHelpers";

    /// <summary>Gets the mobile document schema; gestures remain enabled regardless of this preference.</summary>
    public static SettingsSchema Schema { get; } = new(1,
        [new SettingFieldDeclaration(MouseHelpersKey, SettingValueType.Boolean, "true", SettingSensitivity.Public)]);

    /// <summary>Gets /etc/hackeros/mobile.config and its per-user override.</summary>
    public static OsSettingsDocument Document { get; } = new("mobile", Schema);
}
