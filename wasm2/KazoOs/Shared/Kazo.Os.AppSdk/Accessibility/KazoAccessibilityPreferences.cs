namespace Kazo.Os.AppSdk.Accessibility;

/// <summary>
/// The effective accessibility preferences of the session (ADR 0105): what <c>accessibility.config</c>
/// asks for, with every <c>auto</c> value already resolved against the host's own settings
/// (<c>prefers-contrast</c>, <c>forced-colors</c>, <c>prefers-reduced-motion</c>).
/// </summary>
/// <param name="HighContrast">The high-contrast layer is on: text contrast of at least 7:1 and a 3 px focus indicator.</param>
/// <param name="TextScale">The text size factor: <c>1.0</c>, <c>1.25</c>, <c>1.5</c> or <c>2.0</c>.</param>
/// <param name="ReducedMotion">Non-essential animations are off (blinking cursors, smooth scrolling, transitions).</param>
public sealed record KazoAccessibilityPreferences(bool HighContrast, double TextScale, bool ReducedMotion)
{
    /// <summary>The text size factors <c>accessibility.config</c> accepts, smallest first.</summary>
    public static IReadOnlyList<double> TextScales { get; } = [1.0, 1.25, 1.5, 2.0];

    /// <summary>The preferences of a machine with nothing configured and a host that asks for nothing.</summary>
    public static KazoAccessibilityPreferences Default { get; } = new(HighContrast: false, TextScale: 1.0, ReducedMotion: false);

    /// <summary>
    /// Scales a size in CSS pixels (a terminal or editor font, a line height) by <see cref="TextScale"/>,
    /// rounded to the nearest pixel as a font renderer does.
    /// </summary>
    /// <param name="pixels">The size at <c>textScale = 1.0</c>.</param>
    public int Scale(double pixels) => (int)Math.Round(pixels * TextScale, MidpointRounding.AwayFromZero);
}
