using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.AppSdk.Settings;

/// <summary>
/// One setting as the caller may see it (ADR 0100 section 1): its effective value, or a masked
/// value when it lives in a private file the caller may not read.
/// </summary>
/// <param name="QualifiedKey"><c>key</c> or <c>Group.key</c>, as declared.</param>
/// <param name="Value">Effective serialized value; <see langword="null"/> when <paramref name="IsMasked"/>.</param>
/// <param name="Sensitivity">Declared sensitivity of the key.</param>
/// <param name="Source">Layer that supplied the value (or hides it, when masked).</param>
/// <param name="IsLocked">Whether the system layer locks the key.</param>
/// <param name="IsMasked">Whether the value is hidden from the caller.</param>
public readonly record struct KazoSettingValue(
    string QualifiedKey,
    string? Value,
    SettingSensitivity Sensitivity,
    SettingLayer Source,
    bool IsLocked,
    bool IsMasked)
{
    /// <summary>Text shown in place of a masked or secret value, as Settings does.</summary>
    public const string MaskText = "••••••••";

    /// <summary>
    /// Gets the text to display: <see cref="MaskText"/> for a masked key and for a
    /// <see cref="SettingSensitivity.SecretReference"/> key (whose value only names a keyring entry),
    /// otherwise the value.
    /// </summary>
    public string DisplayText => IsMasked || Sensitivity == SettingSensitivity.SecretReference
        ? MaskText
        : Value ?? string.Empty;
}

/// <summary>
/// A typed read of a setting whose value is masked for the caller (ADR 0100 section 1). Derives
/// from <see cref="UnauthorizedAccessException"/>; <see cref="KazoSettingsDocument.GetValue"/> never throws it.
/// </summary>
public sealed class KazoSettingMaskedException : UnauthorizedAccessException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="qualifiedKey">Masked key.</param>
    /// <param name="privatePath">Private file that holds the hidden value.</param>
    public KazoSettingMaskedException(string qualifiedKey, string privatePath)
        : base($"Setting '{qualifiedKey}' is stored in '{privatePath}', which this caller may not read.")
    {
        QualifiedKey = qualifiedKey;
        PrivatePath = privatePath;
    }

    /// <summary>Gets the masked key.</summary>
    public string QualifiedKey { get; }

    /// <summary>Gets the private file that holds the hidden value.</summary>
    public string PrivatePath { get; }
}
