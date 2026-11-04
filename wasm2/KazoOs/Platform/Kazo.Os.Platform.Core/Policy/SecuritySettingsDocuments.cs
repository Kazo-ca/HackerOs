using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Policy;

/// <summary>
/// What the OS does when a non-system app declares a dangerous capability (ADR 0049 section 3,
/// key <c>dangerousCapabilities</c> of <c>/etc/hackeros/security.config</c>).
/// </summary>
public enum DangerousCapabilityPolicy
{
    /// <summary>
    /// <c>prompt</c> (default): a pending dangerous capability opens the administrator approval
    /// overlay at launch, and a machine <c>allow</c> grants it.
    /// </summary>
    Prompt = 0,

    /// <summary>
    /// <c>block</c>: dangerous capabilities are never granted to non-system apps; existing
    /// <c>allow</c> decisions are ignored and the one-shot approval of ADR 0042 is not offered for them.
    /// </summary>
    Block = 1
}

/// <summary>
/// Defines the administrator security document <c>/etc/hackeros/security.config</c> (ADR 0049
/// section 3).
/// </summary>
/// <remarks>
/// System layer only, like <see cref="PolicySettingsDocuments"/>: the file belongs to
/// <c>system:system</c> with mode <c>0644</c>, so only an administrator (through <c>sudo</c>, the
/// approval prompt of ADR 0042 or the Settings permissions page) can change it. An invalid value is
/// ignored with a warning in <c>/var/log/syslog</c> (ADR 0044), so <c>prompt</c> applies.
/// </remarks>
public static class SecuritySettingsDocuments
{
    /// <summary>Document id, giving <c>security.config</c>.</summary>
    public const string DocumentId = "security";

    /// <summary>Key choosing the <see cref="DangerousCapabilityPolicy"/>.</summary>
    public const string DangerousCapabilitiesKey = "dangerousCapabilities";

    /// <summary>Serialized value of <see cref="DangerousCapabilityPolicy.Prompt"/>.</summary>
    public const string PromptValue = "prompt";

    /// <summary>Serialized value of <see cref="DangerousCapabilityPolicy.Block"/>.</summary>
    public const string BlockValue = "block";

    /// <summary>Gets the declared schema for the security document.</summary>
    public static SettingsSchema Schema { get; } = new(
        schemaVersion: 1,
        fields:
        [
            new SettingFieldDeclaration(
                DangerousCapabilitiesKey,
                SettingValueType.Enum,
                DefaultValue: PromptValue,
                SettingSensitivity.Public,
                AllowedValues: [PromptValue, BlockValue])
        ]);

    /// <summary>Gets the system-only layered document.</summary>
    public static OsSettingsDocument Document { get; } = new(DocumentId, Schema, SystemOnly: true);

    /// <summary>
    /// Gets the commented file a fresh installation ships (written once by
    /// <see cref="SettingsStoreToFilesMigration.MigrateSystemAsync"/>).
    /// </summary>
    public static string DefaultContent { get; } =
        "# /etc/hackeros/security.config\n"
        + "# prompt : une app non système qui demande une capacité dangereuse ouvre la\n"
        + "#          fenêtre d'approbation administrateur (défaut).\n"
        + "# block  : les capacités dangereuses ne sont jamais accordées aux apps non système.\n"
        + "schemaVersion=1\n"
        + $"{DangerousCapabilitiesKey}={PromptValue}\n";

    /// <summary>Converts a policy to its file value.</summary>
    /// <param name="policy">Policy to serialize.</param>
    public static string ToValue(DangerousCapabilityPolicy policy) =>
        policy == DangerousCapabilityPolicy.Block ? BlockValue : PromptValue;

    /// <summary>Reads the effective policy; a missing or invalid file gives <see cref="DangerousCapabilityPolicy.Prompt"/>.</summary>
    /// <param name="settings">OS settings service (system-layer reads are audited system operations).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async ValueTask<DangerousCapabilityPolicy> ReadPolicyAsync(
        IOsSettingsService settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        LayeredConfigSnapshot snapshot = await settings.ReadAsync(Document, cancellationToken).ConfigureAwait(false);
        return snapshot.Values.TryGetValue(DangerousCapabilitiesKey, out string? value)
            && string.Equals(value, BlockValue, StringComparison.Ordinal)
            ? DangerousCapabilityPolicy.Block
            : DangerousCapabilityPolicy.Prompt;
    }
}
