namespace HackerOs.Theming.Abstractions;

/// <summary>Stores the independent desktop, mobile, accent, and animation appearance choices.</summary>
/// <param name="DesktopThemeId">Persisted desktop theme identifier.</param>
/// <param name="MobileThemeId">Persisted mobile theme identifier.</param>
/// <param name="AccentId">Persisted accent identifier.</param>
/// <param name="AnimationsEnabled">Whether non-essential shell animations are enabled.</param>
public sealed record ThemePreferences(
    string DesktopThemeId,
    string MobileThemeId,
    string AccentId,
    bool AnimationsEnabled)
{
    /// <summary>Gets the clean-profile KazoOS/Android/green preferences with animations enabled.</summary>
    public static ThemePreferences Default { get; } = new(
        WellKnownThemeIds.HackerOs,
        WellKnownThemeIds.Android,
        WellKnownAccentIds.Green,
        AnimationsEnabled: true);
}
