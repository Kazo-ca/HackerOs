namespace Kazo.Os.AppSdk.Settings;

/// <summary>
/// Thrown when an app writes the user layer of a key that an administrator locked in the system
/// layer's <c>[locked]</c> section (ADR 0044 section 3). The user layer is ignored for such a key,
/// so accepting the write would silently have no effect.
/// </summary>
public sealed class HackSettingLockedException : InvalidOperationException
{
    /// <summary>Creates the exception for one locked key.</summary>
    /// <param name="qualifiedKey">Qualified key (<c>key</c> or <c>Group.key</c>).</param>
    /// <param name="systemPath">System file whose <c>[locked]</c> section locks the key.</param>
    public HackSettingLockedException(string qualifiedKey, string systemPath)
        : base($"Setting '{qualifiedKey}' is locked by the administrator in {systemPath}.")
    {
        QualifiedKey = qualifiedKey;
        SystemPath = systemPath;
    }

    /// <summary>Gets the locked qualified key.</summary>
    public string QualifiedKey { get; }

    /// <summary>Gets the system file that locks the key.</summary>
    public string SystemPath { get; }
}
