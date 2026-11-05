using Kazo.Os.Theming.Abstractions;

namespace Kazo.Os.Platform.Core.Tests.Appearance;

public sealed class ThemeCatalogTests
{
    [Fact]
    public void Catalog_exposes_requested_themes_in_deterministic_platform_order()
    {
        Assert.Equal(
            [
                WellKnownThemeIds.KazoOs,
                WellKnownThemeIds.Windows98,
                WellKnownThemeIds.WindowsXp,
                WellKnownThemeIds.Windows7,
                WellKnownThemeIds.Windows10,
                WellKnownThemeIds.MacOs,
                WellKnownThemeIds.Ubuntu
            ],
            ThemeCatalog.DesktopThemes.Select(theme => theme.Id));

        Assert.Equal(
            [WellKnownThemeIds.Android, WellKnownThemeIds.Ios],
            ThemeCatalog.MobileThemes.Select(theme => theme.Id));
        Assert.All(ThemeCatalog.DesktopThemes, theme => Assert.Equal(ThemePlatform.Desktop, theme.Platform));
        Assert.All(ThemeCatalog.MobileThemes, theme => Assert.Equal(ThemePlatform.Mobile, theme.Platform));
    }

    [Fact]
    public void Lookup_and_accents_are_exact_and_bounded()
    {
        Assert.True(ThemeCatalog.TryGet(WellKnownThemeIds.Windows7, out ThemeDefinition? windows7));
        Assert.Same(windows7, ThemeCatalog.Get(WellKnownThemeIds.Windows7));
        Assert.False(ThemeCatalog.TryGet("Windows-7", out _));
        Assert.Equal(
            [WellKnownAccentIds.Green, WellKnownAccentIds.Cyan, WellKnownAccentIds.Purple],
            ThemeCatalog.AccentIds);
        Assert.False(ThemeCatalog.IsKnownAccent("chartreuse"));
    }
}
