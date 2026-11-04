using System.Globalization;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Appearance;

/// <summary>A three-way accessibility switch of <c>accessibility.config</c> (ADR 0105 section 1).</summary>
public enum AccessibilityMode
{
    /// <summary>Follows the host: its own high-contrast or reduced-motion setting decides.</summary>
    Auto = 0,

    /// <summary>Always on, whatever the host says.</summary>
    On = 1,

    /// <summary>Always off, whatever the host says.</summary>
    Off = 2
}

/// <summary>
/// What <c>accessibility.config</c> asks for, before <c>auto</c> is resolved (ADR 0105 section 1).
/// </summary>
/// <param name="HighContrast">The <c>highContrast</c> key.</param>
/// <param name="TextScale">The <c>textScale</c> key: <c>1.0</c>, <c>1.25</c>, <c>1.5</c> or <c>2.0</c>.</param>
/// <param name="ReducedMotion">The <c>reducedMotion</c> key.</param>
public sealed record AccessibilitySettings(AccessibilityMode HighContrast, double TextScale, AccessibilityMode ReducedMotion)
{
    /// <summary>A machine with nothing configured: everything follows the host, text at 100 %.</summary>
    public static AccessibilitySettings Default { get; } = new(AccessibilityMode.Auto, 1.0, AccessibilityMode.Auto);
}

/// <summary>
/// The host's own accessibility settings, as the browser reports them through media queries.
/// </summary>
/// <param name="PrefersMoreContrast"><c>prefers-contrast: more</c> matches.</param>
/// <param name="ForcedColors"><c>forced-colors: active</c> matches (Windows high-contrast themes).</param>
/// <param name="PrefersReducedMotion"><c>prefers-reduced-motion: reduce</c> matches.</param>
public sealed record HostAccessibilitySignals(bool PrefersMoreContrast, bool ForcedColors, bool PrefersReducedMotion)
{
    /// <summary>A host that asks for nothing (and any host whose signals cannot be read).</summary>
    public static HostAccessibilitySignals None { get; } = new(false, false, false);
}

/// <summary>
/// Defines the layered accessibility document (ADR 0105 section 1):
/// <c>/etc/kazoos/accessibility.config</c> holds the machine default (also used by the sign-in
/// screen, before any user is known), and each user overrides it in
/// <c>~/.config/kazoos/accessibility.config</c>.
/// </summary>
public static class AccessibilitySettingsDocuments
{
    /// <summary>Key of the high-contrast switch: <c>auto</c>, <c>on</c> or <c>off</c>.</summary>
    public const string HighContrastKey = "highContrast";

    /// <summary>Key of the text size factor: <c>1.0</c>, <c>1.25</c>, <c>1.5</c> or <c>2.0</c>.</summary>
    public const string TextScaleKey = "textScale";

    /// <summary>Key of the reduced-motion switch: <c>auto</c>, <c>on</c> or <c>off</c>.</summary>
    public const string ReducedMotionKey = "reducedMotion";

    /// <summary>The values of <see cref="TextScaleKey"/>, as written in the file.</summary>
    public static IReadOnlyList<string> TextScaleValues { get; } = ["1.0", "1.25", "1.5", "2.0"];

    private static readonly string[] ModeValues = ["auto", "on", "off"];

    /// <summary>Gets the declared schema: two closed three-way switches and a closed set of text sizes.</summary>
    public static SettingsSchema Schema { get; } = new(
        schemaVersion: 1,
        fields:
        [
            new SettingFieldDeclaration(HighContrastKey, SettingValueType.Enum, "auto", SettingSensitivity.Public, ModeValues),
            new SettingFieldDeclaration(TextScaleKey, SettingValueType.Enum, "1.0", SettingSensitivity.Public, [.. TextScaleValues]),
            new SettingFieldDeclaration(ReducedMotionKey, SettingValueType.Enum, "auto", SettingSensitivity.Public, ModeValues)
        ]);

    /// <summary>Gets the layered document.</summary>
    public static OsSettingsDocument Document { get; } = new("accessibility", Schema);

    /// <summary>Builds the settings from a merged snapshot (always valid: invalid lines fell back to the next layer).</summary>
    /// <param name="snapshot">Snapshot of <see cref="Document"/>.</param>
    public static AccessibilitySettings ToSettings(LayeredConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new AccessibilitySettings(
            ParseMode(snapshot.Values[HighContrastKey]),
            ParseTextScale(snapshot.Values[TextScaleKey]),
            ParseMode(snapshot.Values[ReducedMotionKey]));
    }

    /// <summary>Gets the file spelling of a mode: <c>auto</c>, <c>on</c> or <c>off</c>.</summary>
    /// <param name="mode">The mode.</param>
    public static string FormatMode(AccessibilityMode mode) => mode switch
    {
        AccessibilityMode.On => "on",
        AccessibilityMode.Off => "off",
        _ => "auto"
    };

    /// <summary>Gets the file spelling of a text size factor (<c>1.0</c>, <c>1.25</c>…), whatever the culture.</summary>
    /// <param name="scale">One of the accepted factors.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not an accepted factor.</exception>
    public static string FormatTextScale(double scale) =>
        TextScaleValues.FirstOrDefault(value => ParseTextScale(value) == scale)
        ?? throw new ArgumentOutOfRangeException(nameof(scale), scale, "The text scale must be 1.0, 1.25, 1.5 or 2.0.");

    /// <summary>
    /// Resolves the settings against the host (ADR 0105 section 1): <c>auto</c> high contrast follows
    /// <c>prefers-contrast: more</c> or <c>forced-colors: active</c>, <c>auto</c> reduced motion follows
    /// <c>prefers-reduced-motion</c>, and <c>animationsEnabled = false</c> of <c>appearance.config</c>
    /// counts as <c>reducedMotion = on</c>.
    /// </summary>
    /// <param name="settings">What the files ask for.</param>
    /// <param name="host">What the host asks for.</param>
    /// <param name="animationsEnabled">The <c>animationsEnabled</c> key of <c>appearance.config</c>.</param>
    public static AppSdk.Accessibility.KazoAccessibilityPreferences Resolve(
        AccessibilitySettings settings, HostAccessibilitySignals host, bool animationsEnabled)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(host);
        bool highContrast = settings.HighContrast switch
        {
            AccessibilityMode.On => true,
            AccessibilityMode.Off => false,
            _ => host.PrefersMoreContrast || host.ForcedColors
        };
        bool reducedMotion = !animationsEnabled || settings.ReducedMotion switch
        {
            AccessibilityMode.On => true,
            AccessibilityMode.Off => false,
            _ => host.PrefersReducedMotion
        };
        return new AppSdk.Accessibility.KazoAccessibilityPreferences(highContrast, settings.TextScale, reducedMotion);
    }

    private static AccessibilityMode ParseMode(string value) => value switch
    {
        "on" => AccessibilityMode.On,
        "off" => AccessibilityMode.Off,
        _ => AccessibilityMode.Auto
    };

    private static double ParseTextScale(string value) =>
        double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double scale) ? scale : 1.0;
}
