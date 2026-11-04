using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Policy;

/// <summary>
/// Defines the administrator policy document <c>/etc/hackeros/policy.config</c> (ADR 0044 section 8).
/// </summary>
/// <remarks>
/// System layer only: a user's <c>~/.config/hackeros/policy.config</c> is never read. The file
/// belongs to <c>system:system</c> with mode <c>0644</c>, so only an administrator acting through
/// <c>sudo</c> or an approval ticket (ADR 0042) can change it, exactly like any other file in
/// <c>/etc</c>.
/// </remarks>
public static class PolicySettingsDocuments
{
    /// <summary>Document id, giving <c>policy.config</c>.</summary>
    public const string DocumentId = "policy";

    /// <summary>Gets the declared schema for the policy document.</summary>
    /// <remarks>
    /// <c>capabilityGrantsLocked</c> was removed by ADR 0049: it was never evaluated. Dangerous
    /// capabilities are governed by <c>/etc/hackeros/security.config</c> instead.
    /// </remarks>
    public static SettingsSchema Schema { get; } = new(
        schemaVersion: 1,
        fields:
        [
            new SettingFieldDeclaration(
                "allowRuntimePackageInstall",
                SettingValueType.Boolean,
                DefaultValue: "false",
                SettingSensitivity.Public)
        ]);

    /// <summary>Gets the system-only layered document.</summary>
    public static OsSettingsDocument Document { get; } = new(DocumentId, Schema, SystemOnly: true);
}
