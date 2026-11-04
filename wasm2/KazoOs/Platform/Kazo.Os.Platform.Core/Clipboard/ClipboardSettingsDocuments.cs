using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Clipboard;

/// <summary>
/// Which way text crosses between the Kazo OS clipboard and the real machine's clipboard (ADR 0065),
/// the "Shared Clipboard" setting of VirtualBox.
/// </summary>
public enum ClipboardSharingMode
{
    /// <summary>Both ways (default).</summary>
    Bidirectional,

    /// <summary>Only from Kazo OS to the host: what an app copies reaches the host, a host paste does not enter.</summary>
    GuestToHost,

    /// <summary>Only from the host to Kazo OS.</summary>
    HostToGuest,

    /// <summary>Neither way: Kazo OS keeps its own clipboard.</summary>
    Disabled
}

/// <summary>Direction tests of <see cref="ClipboardSharingMode"/>.</summary>
public static class ClipboardSharingModeExtensions
{
    /// <summary>Whether text copied in Kazo OS is written to the host's clipboard.</summary>
    /// <param name="mode">Sharing mode.</param>
    public static bool AllowsGuestToHost(this ClipboardSharingMode mode) =>
        mode is ClipboardSharingMode.Bidirectional or ClipboardSharingMode.GuestToHost;

    /// <summary>Whether a paste from the host replaces the Kazo OS clipboard.</summary>
    /// <param name="mode">Sharing mode.</param>
    public static bool AllowsHostToGuest(this ClipboardSharingMode mode) =>
        mode is ClipboardSharingMode.Bidirectional or ClipboardSharingMode.HostToGuest;
}

/// <summary>
/// Defines the layered clipboard document (ADR 0065, ADR 0044): <c>/etc/kazoos/clipboard.config</c>
/// holds the machine default, each user may override it in <c>~/.config/kazoos/clipboard.config</c>,
/// and an administrator forces it by putting the key under <c>[locked]</c> in the system file.
/// </summary>
public static class ClipboardSettingsDocuments
{
    /// <summary>Key of the sharing direction.</summary>
    public const string SharingKey = "sharing";

    private static readonly (string Value, ClipboardSharingMode Mode)[] Modes =
    [
        ("bidirectional", ClipboardSharingMode.Bidirectional),
        ("guest-to-host", ClipboardSharingMode.GuestToHost),
        ("host-to-guest", ClipboardSharingMode.HostToGuest),
        ("disabled", ClipboardSharingMode.Disabled)
    ];

    /// <summary>Gets the declared schema: <c>sharing</c> is one of the four modes, <c>bidirectional</c> by default.</summary>
    public static SettingsSchema Schema { get; } = new(
        schemaVersion: 1,
        fields:
        [
            new SettingFieldDeclaration(
                SharingKey,
                SettingValueType.Enum,
                "bidirectional",
                SettingSensitivity.Public,
                [.. Modes.Select(static mode => mode.Value)])
        ]);

    /// <summary>Gets the layered document.</summary>
    public static OsSettingsDocument Document { get; } = new("clipboard", Schema);

    /// <summary>Gets the file value of a mode (<c>guest-to-host</c>…).</summary>
    /// <param name="mode">Sharing mode.</param>
    public static string ToValue(ClipboardSharingMode mode) => Modes.First(entry => entry.Mode == mode).Value;

    /// <summary>Reads the mode of a value; an unknown value gives the default, <see cref="ClipboardSharingMode.Bidirectional"/>.</summary>
    /// <param name="value">File value.</param>
    public static ClipboardSharingMode ParseMode(string? value)
    {
        foreach ((string text, ClipboardSharingMode mode) in Modes)
        {
            if (string.Equals(text, value, StringComparison.Ordinal))
            {
                return mode;
            }
        }

        return ClipboardSharingMode.Bidirectional;
    }

    /// <summary>Gets the effective mode of a merged snapshot (always valid: invalid lines fell back to the next layer).</summary>
    /// <param name="snapshot">Snapshot of <see cref="Document"/>.</param>
    public static ClipboardSharingMode ToMode(LayeredConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return ParseMode(snapshot.Values[SharingKey]);
    }
}
