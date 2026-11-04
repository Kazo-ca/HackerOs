using Kazo.Os.App.Abstractions;

namespace Kazo.Os.App.Abstractions.Tests;

public sealed class AppPlatformCapabilitiesTests
{
    [Fact]
    public void Constructor_rejects_blank_shell_family()
    {
        Assert.Throws<ArgumentException>(() => new AppPlatformCapabilities(
            WellKnownAppPlatforms.Desktop,
            shellFamily: "  ",
            supportsFloatingWindows: true,
            maxVisiblePrimarySurfaces: null,
            supportsMove: true,
            supportsResize: true,
            supportsMinimize: true,
            supportsMaximize: true,
            systemNavigation: PlatformSystemNavigationKind.DesktopTaskbar,
            hasApplicationBar: true,
            keyboardStrategy: PlatformKeyboardStrategy.PhysicalOnly));
    }

    [Fact]
    public void Constructor_rejects_zero_max_visible_surfaces()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AppPlatformCapabilities(
            WellKnownAppPlatforms.Mobile,
            shellFamily: "mobile-single-surface",
            supportsFloatingWindows: false,
            maxVisiblePrimarySurfaces: 0,
            supportsMove: false,
            supportsResize: false,
            supportsMinimize: false,
            supportsMaximize: false,
            systemNavigation: PlatformSystemNavigationKind.AndroidStyleSystemBar,
            hasApplicationBar: false,
            keyboardStrategy: PlatformKeyboardStrategy.VirtualKazoOsKeyboard));
    }

    [Fact]
    public void Constructor_rejects_non_positive_minimum_viewport_width()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AppPlatformCapabilities(
            WellKnownAppPlatforms.Mobile,
            shellFamily: "mobile-single-surface",
            supportsFloatingWindows: false,
            maxVisiblePrimarySurfaces: 1,
            supportsMove: false,
            supportsResize: false,
            supportsMinimize: false,
            supportsMaximize: false,
            systemNavigation: PlatformSystemNavigationKind.AndroidStyleSystemBar,
            hasApplicationBar: false,
            keyboardStrategy: PlatformKeyboardStrategy.VirtualKazoOsKeyboard,
            minimumViewportWidth: -1));
    }

    [Fact]
    public void WellKnown_desktop_supports_unbounded_floating_windows()
    {
        AppPlatformCapabilities desktop = WellKnownAppPlatformCapabilities.Desktop;

        Assert.Equal(WellKnownAppPlatforms.Desktop, desktop.PlatformId);
        Assert.True(desktop.SupportsFloatingWindows);
        Assert.Null(desktop.MaxVisiblePrimarySurfaces);
        Assert.True(desktop.SupportsMove);
        Assert.True(desktop.SupportsResize);
        Assert.True(desktop.SupportsMinimize);
        Assert.True(desktop.SupportsMaximize);
        Assert.Equal(PlatformSystemNavigationKind.DesktopTaskbar, desktop.SystemNavigation);
        Assert.True(desktop.HasApplicationBar);
        Assert.Equal(PlatformKeyboardStrategy.PhysicalOnly, desktop.KeyboardStrategy);
        Assert.False(desktop.RequiresSafeAreaInsets);
    }

    [Fact]
    public void WellKnown_mobile_is_a_single_non_floating_surface()
    {
        AppPlatformCapabilities mobile = WellKnownAppPlatformCapabilities.Mobile;

        Assert.Equal(WellKnownAppPlatforms.Mobile, mobile.PlatformId);
        Assert.False(mobile.SupportsFloatingWindows);
        Assert.Equal(1, mobile.MaxVisiblePrimarySurfaces);
        Assert.False(mobile.SupportsMove);
        Assert.False(mobile.SupportsResize);
        Assert.False(mobile.SupportsMinimize);
        Assert.False(mobile.SupportsMaximize);
        Assert.Equal(PlatformSystemNavigationKind.AndroidStyleSystemBar, mobile.SystemNavigation);
        Assert.False(mobile.HasApplicationBar);
        Assert.Equal(PlatformKeyboardStrategy.VirtualKazoOsKeyboard, mobile.KeyboardStrategy);
        Assert.True(mobile.RequiresSafeAreaInsets);
    }

    [Fact]
    public void Registry_resolves_well_known_platforms()
    {
        AppPlatformCapabilityRegistry registry = AppPlatformCapabilityRegistry.CreateWithWellKnownPlatforms();

        Assert.True(registry.TryGet(WellKnownAppPlatforms.Desktop, out AppPlatformCapabilities? desktop));
        Assert.Same(WellKnownAppPlatformCapabilities.Desktop, desktop);

        Assert.True(registry.TryGet(WellKnownAppPlatforms.Mobile, out AppPlatformCapabilities? mobile));
        Assert.Same(WellKnownAppPlatformCapabilities.Mobile, mobile);

        Assert.Equal(2, registry.KnownPlatforms.Count);
    }

    [Fact]
    public void Registry_TryGet_returns_false_for_unknown_platform()
    {
        AppPlatformCapabilityRegistry registry = new();

        Assert.False(registry.TryGet(AppPlatformId.Parse("desktop-vr"), out AppPlatformCapabilities? capabilities));
        Assert.Null(capabilities);
    }

    [Fact]
    public void Registry_rejects_duplicate_registration()
    {
        AppPlatformCapabilityRegistry registry = new();
        registry.Register(WellKnownAppPlatformCapabilities.Desktop);

        Assert.Throws<ArgumentException>(() => registry.Register(WellKnownAppPlatformCapabilities.Desktop));
    }
}
