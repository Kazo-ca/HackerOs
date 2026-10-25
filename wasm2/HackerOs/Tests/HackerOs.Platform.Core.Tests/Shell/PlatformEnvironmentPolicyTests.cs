using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Shell;

namespace HackerOs.Platform.Core.Tests.Shell;

public sealed class PlatformEnvironmentPolicyTests
{
    [Fact]
    public void Touch_coarse_no_hover_narrow_viewport_suggests_mobile()
    {
        PlatformEnvironmentSignals signals = new(
            LogicalWidth: 390, LogicalHeight: 844, PointerIsCoarse: true, HasHover: false, MaxTouchPoints: 5, IsStandalone: false);

        PlatformEnvironmentSnapshot snapshot = PlatformEnvironmentPolicy.Decide(signals);

        Assert.Equal(WellKnownAppPlatforms.Mobile, snapshot.SuggestedPlatform);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Reason));
    }

    [Fact]
    public void Fine_hover_pointer_suggests_desktop()
    {
        PlatformEnvironmentSignals signals = new(
            LogicalWidth: 1280, LogicalHeight: 800, PointerIsCoarse: false, HasHover: true, MaxTouchPoints: 0, IsStandalone: false);

        PlatformEnvironmentSnapshot snapshot = PlatformEnvironmentPolicy.Decide(signals);

        Assert.Equal(WellKnownAppPlatforms.Desktop, snapshot.SuggestedPlatform);
    }

    [Fact]
    public void Narrow_desktop_window_with_no_touch_points_stays_desktop()
    {
        // A Desktop browser window resized narrow must not be mistaken for a mobile device
        // (doc/wasm/dev/old/mobile-interface-platform-plan.md §6.2).
        PlatformEnvironmentSignals signals = new(
            LogicalWidth: 320, LogicalHeight: 700, PointerIsCoarse: false, HasHover: true, MaxTouchPoints: 0, IsStandalone: false);

        PlatformEnvironmentSnapshot snapshot = PlatformEnvironmentPolicy.Decide(signals);

        Assert.Equal(WellKnownAppPlatforms.Desktop, snapshot.SuggestedPlatform);
    }

    [Fact]
    public void Touch_capable_wide_viewport_stays_desktop()
    {
        // A touch-screen laptop: coarse pointer and touch points, but a wide viewport.
        PlatformEnvironmentSignals signals = new(
            LogicalWidth: 1366, LogicalHeight: 768, PointerIsCoarse: true, HasHover: false, MaxTouchPoints: 10, IsStandalone: false);

        PlatformEnvironmentSnapshot snapshot = PlatformEnvironmentPolicy.Decide(signals);

        Assert.Equal(WellKnownAppPlatforms.Desktop, snapshot.SuggestedPlatform);
    }

    [Fact]
    public void Hover_capable_touch_device_stays_desktop()
    {
        // Some hybrid devices report touch points but retain hover (e.g. touch + trackpad).
        PlatformEnvironmentSignals signals = new(
            LogicalWidth: 390, LogicalHeight: 844, PointerIsCoarse: true, HasHover: true, MaxTouchPoints: 5, IsStandalone: false);

        PlatformEnvironmentSnapshot snapshot = PlatformEnvironmentPolicy.Decide(signals);

        Assert.Equal(WellKnownAppPlatforms.Desktop, snapshot.SuggestedPlatform);
    }
}
